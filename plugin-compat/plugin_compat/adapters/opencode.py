"""OpenCode harness adapter — translates PluginDefinition → OpenCode bundle.

One adapter for all plugins. Never branches on plugin names.
"""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from ..harness import get_harness_capabilities
from ..model import CompatibilityResult, CompatibilityStatus, PluginDefinition
from ..translate import (
    render_frontmatter,
    translate_agent_frontmatter,
    translate_body,
    translate_command_frontmatter,
    translate_skill_frontmatter,
)
from ..validator import validate_compatibility

ADAPTER_VERSION = "0.2.0"
HARNESS = "opencode"

# Claude-only assets that must never be registered as OpenCode plugins.
_SKIP_COPY_NAMES = frozenset(
    {
        "plugin.json",
        "hooks.json",
        ".lsp.json",
    }
)


def _safe_dest(output: Path, relative: str | Path) -> Path:
    """Resolve ``output / relative`` and reject paths that escape ``output``."""
    dest = (output / relative).resolve()
    output_resolved = output.resolve()
    try:
        dest.relative_to(output_resolved)
    except ValueError as exc:
        raise ValueError(f"Invalid path traversal attempt: {relative}") from exc
    return dest


@dataclass
class GeneratedBundle:
    """Result of adapting a PluginDefinition for OpenCode."""

    plugin_id: str
    plugin_version: str
    output_dir: Path
    compatibility: CompatibilityResult
    commands: list[str] = field(default_factory=list)
    agents: list[str] = field(default_factory=list)
    skills: list[str] = field(default_factory=list)
    scripts_copied: int = 0
    templates_copied: int = 0
    lead_command_path: str | None = None
    cache_hit: bool = False
    adapter_version: str = ADAPTER_VERSION

    def version_info(self) -> dict[str, Any]:
        return {
            "harness": HARNESS,
            "plugin": self.plugin_id,
            "plugin_version": self.plugin_version,
            "adapter_version": self.adapter_version,
            "cache_hit": self.cache_hit,
            "compatibility": self.compatibility.status.value,
            "commands": list(self.commands),
            "agents": list(self.agents),
            "skills": list(self.skills),
            "output_dir": str(self.output_dir),
            "lead_command_path": self.lead_command_path,
        }


class OpenCodePluginAdapter:
    """Translate a harness-neutral PluginDefinition into an OpenCode runtime bundle."""

    def __init__(self, *, adapter_version: str = ADAPTER_VERSION) -> None:
        self.adapter_version = adapter_version
        self.harness = get_harness_capabilities(HARNESS)

    def adapt(
        self,
        plugin: PluginDefinition,
        output_dir: str | Path,
        *,
        metadata_path: str | Path | None = None,
        fail_on_unsupported: bool = True,
        force: bool = False,
    ) -> GeneratedBundle:
        """Generate an OpenCode bundle under ``output_dir``.

        Raises ``CompatibilityError`` when status is UNSUPPORTED and
        ``fail_on_unsupported`` is True.
        """
        output = Path(output_dir)
        compatibility = validate_compatibility(
            plugin, HARNESS, metadata_path=metadata_path
        )

        if (
            fail_on_unsupported
            and compatibility.status == CompatibilityStatus.UNSUPPORTED
        ):
            gaps = ", ".join(c.value for c in compatibility.unsupported_required)
            raise CompatibilityError(
                f"Plugin '{plugin.id}' is UNSUPPORTED on OpenCode "
                f"(required unsupported: {gaps})"
            )

        cache_key = self._cache_key(plugin)
        version_path = output / "VERSION.json"
        if (
            not force
            and version_path.is_file()
            and self._bundle_matches_cache(version_path, cache_key)
        ):
            return self._load_cached_bundle(plugin, output, compatibility)

        if output.exists():
            shutil.rmtree(output)
        output.mkdir(parents=True, exist_ok=True)

        opencode_dir = output / ".opencode"
        commands_dir = opencode_dir / "commands"
        agents_dir = opencode_dir / "agents"
        skills_dir = opencode_dir / "skills"
        commands_dir.mkdir(parents=True, exist_ok=True)
        agents_dir.mkdir(parents=True, exist_ok=True)
        skills_dir.mkdir(parents=True, exist_ok=True)

        command_names: list[str] = []
        for command in plugin.commands:
            name = _safe_filename(command.id or Path(command.path).stem)
            dest = commands_dir / f"{name}.md"
            fm = translate_command_frontmatter(command.frontmatter)
            body = translate_body(command.body, kind="command")
            # Ensure scripts resolve via CLAUDE_PLUGIN_ROOT (= bundle root).
            body = self._ensure_root_hint(body)
            dest.write_text(render_frontmatter(fm) + body, encoding="utf-8")
            command_names.append(name)

        agent_names: list[str] = []
        for agent in plugin.agents:
            name = _safe_filename(agent.id or Path(agent.path).stem)
            dest = agents_dir / f"{name}.md"
            fm = translate_agent_frontmatter(agent.frontmatter)
            body = translate_body(agent.body, kind="agent")
            dest.write_text(render_frontmatter(fm) + body, encoding="utf-8")
            agent_names.append(name)

        skill_names: list[str] = []
        for skill in plugin.skills:
            skill_id = _safe_filename(skill.id or Path(skill.path).parent.name)
            skill_dir = skills_dir / skill_id
            skill_dir.mkdir(parents=True, exist_ok=True)
            dest = skill_dir / "SKILL.md"
            fm = translate_skill_frontmatter(skill.frontmatter, skill_id)
            # OpenCode requires name == directory name
            fm["name"] = skill_id
            body = translate_body(skill.body, kind="skill")
            dest.write_text(render_frontmatter(fm) + body, encoding="utf-8")
            skill_names.append(skill_id)

        scripts_copied = self._copy_scripts(plugin, output)
        templates_copied = self._copy_templates(plugin, output)
        self._copy_hook_scripts(plugin, output)

        lead = None
        if command_names:
            lead = str((commands_dir / f"{command_names[0]}.md").resolve())

        bundle = GeneratedBundle(
            plugin_id=plugin.id,
            plugin_version=plugin.version,
            output_dir=output.resolve(),
            compatibility=compatibility,
            commands=command_names,
            agents=agent_names,
            skills=skill_names,
            scripts_copied=scripts_copied,
            templates_copied=templates_copied,
            lead_command_path=lead,
            cache_hit=False,
            adapter_version=self.adapter_version,
        )
        self._write_metadata(bundle, plugin, cache_key)
        return bundle

    def _ensure_root_hint(self, body: str) -> str:
        hint = (
            "Plugin root for scripts: set `CLAUDE_PLUGIN_ROOT` to this bundle "
            "directory (the executor does this automatically).\n\n"
        )
        if "CLAUDE_PLUGIN_ROOT" in body and "executor does this" not in body:
            return hint + body
        return body

    def _copy_scripts(self, plugin: PluginDefinition, output: Path) -> int:
        if not plugin.scripts:
            return 0
        # Skip Claude-only resolve-models.sh (model tiers differ on OpenCode).
        skip = {"resolve-models.sh"}
        count = 0
        root = Path(plugin.root)
        for script in plugin.scripts:
            if Path(script.relative_path).name in skip:
                continue
            src = root / script.relative_path
            if not src.is_file():
                continue
            dest = _safe_dest(output, script.relative_path)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dest)
            count += 1
        return count

    def _copy_templates(self, plugin: PluginDefinition, output: Path) -> int:
        count = 0
        root = Path(plugin.root)
        for template in plugin.templates:
            src = root / template.path
            if not src.is_file():
                continue
            dest = _safe_dest(output, template.path)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dest)
            count += 1
        return count

    def _copy_hook_scripts(self, plugin: PluginDefinition, output: Path) -> None:
        """Copy portable hook *script bodies* only — never hooks.json protocol."""
        root = Path(plugin.root)
        hooks_dir = root / "hooks"
        if not hooks_dir.is_dir():
            return
        dest_hooks = output / "hooks"
        dest_hooks.mkdir(parents=True, exist_ok=True)
        for path in hooks_dir.iterdir():
            if not path.is_file():
                continue
            if path.name in _SKIP_COPY_NAMES:
                continue
            if path.suffix.lower() in {".sh", ".py"}:
                shutil.copy2(path, dest_hooks / path.name)

    def _cache_key(self, plugin: PluginDefinition) -> str:
        return (
            f"{plugin.id}:{plugin.version}:{plugin.source_hash}:"
            f"{self.adapter_version}:{HARNESS}"
        )

    def _bundle_matches_cache(self, version_path: Path, cache_key: str) -> bool:
        try:
            data = json.loads(version_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return False
        return data.get("cache_key") == cache_key

    def _load_cached_bundle(
        self,
        plugin: PluginDefinition,
        output: Path,
        compatibility: CompatibilityResult,
    ) -> GeneratedBundle:
        meta_path = output / "VERSION.json"
        data = json.loads(meta_path.read_text(encoding="utf-8"))
        commands = list(data.get("commands") or [])
        lead = None
        if commands:
            candidate = output / ".opencode" / "commands" / f"{commands[0]}.md"
            if candidate.is_file():
                lead = str(candidate.resolve())
        return GeneratedBundle(
            plugin_id=plugin.id,
            plugin_version=plugin.version,
            output_dir=output.resolve(),
            compatibility=compatibility,
            commands=commands,
            agents=list(data.get("agents") or []),
            skills=list(data.get("skills") or []),
            scripts_copied=int(data.get("scripts_copied") or 0),
            templates_copied=int(data.get("templates_copied") or 0),
            lead_command_path=lead,
            cache_hit=True,
            adapter_version=str(data.get("adapter_version") or self.adapter_version),
        )

    def _write_metadata(
        self,
        bundle: GeneratedBundle,
        plugin: PluginDefinition,
        cache_key: str,
    ) -> None:
        payload = {
            "harness": HARNESS,
            "plugin": bundle.plugin_id,
            "plugin_version": bundle.plugin_version,
            "plugin_source_hash": plugin.source_hash,
            "adapter_version": bundle.adapter_version,
            "cache_key": cache_key,
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "compatibility": bundle.compatibility.status.value,
            "commands": bundle.commands,
            "agents": bundle.agents,
            "skills": bundle.skills,
            "scripts_copied": bundle.scripts_copied,
            "templates_copied": bundle.templates_copied,
            "capabilities": [
                {
                    "capability": a.capability.value,
                    "support": a.support.value,
                    "required": a.required,
                }
                for a in bundle.compatibility.assessments
                if a.present
            ],
            "unsupported_required": [
                c.value for c in bundle.compatibility.unsupported_required
            ],
            "partial_or_optional_gaps": [
                c.value for c in bundle.compatibility.partial_or_optional_gaps
            ],
            "constructs": [c.value for c in bundle.compatibility.detected_constructs],
        }
        (bundle.output_dir / "VERSION.json").write_text(
            json.dumps(payload, indent=2) + "\n",
            encoding="utf-8",
        )
        # Human-readable summary for logs/ops
        summary = "\n".join(bundle.compatibility.summary_lines()) + "\n"
        (bundle.output_dir / "COMPATIBILITY.txt").write_text(summary, encoding="utf-8")


class CompatibilityError(RuntimeError):
    """Raised when a plugin cannot run on the target harness."""


def _safe_filename(name: str) -> str:
    cleaned = "".join(c if c.isalnum() or c in "-_" else "-" for c in name.strip())
    cleaned = cleaned.strip("-_") or "asset"
    return cleaned[:64]

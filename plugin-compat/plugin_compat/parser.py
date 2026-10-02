"""Parse existing Claude/Xianix plugin directories into PluginDefinition.

Structure-based only — never branches on plugin names.
"""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any

from .capabilities import detect_capabilities, detect_constructs_in_text
from .constructs import ClaudeConstruct
from .model import (
    AgentDefinition,
    CommandDefinition,
    Frontmatter,
    HookDefinition,
    PluginDefinition,
    ScriptDefinition,
    SkillDefinition,
    TemplateDefinition,
)

_FRONTMATTER_RE = re.compile(r"\A---\s*\n(.*?)\n---\s*\n?(.*)\Z", re.DOTALL)
_CLAUDE_ROOT_IN_CMD = re.compile(
    r"\$\{CLAUDE_PLUGIN_ROOT\}/([^\s\"'`]+)|\$CLAUDE_PLUGIN_ROOT/([^\s\"'`]+)"
)


class PluginParseError(ValueError):
    """Raised when a plugin root cannot be parsed."""


def parse_plugin(plugin_root: str | Path) -> PluginDefinition:
    """Parse a Claude-style plugin directory into a PluginDefinition."""
    root = Path(plugin_root).resolve()
    if not root.is_dir():
        raise PluginParseError(f"Plugin root is not a directory: {root}")

    manifest_path = root / ".claude-plugin" / "plugin.json"
    if not manifest_path.is_file():
        raise PluginParseError(f"Missing plugin manifest: {manifest_path}")

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as exc:
        raise PluginParseError(f"Invalid plugin.json: {exc}") from exc

    if not isinstance(manifest, dict):
        raise PluginParseError("plugin.json must be a JSON object")

    name = str(manifest.get("name") or root.name)
    version = str(manifest.get("version") or "0.0.0")
    description = str(manifest.get("description") or "")

    registered_commands = _normalize_path_list(manifest.get("commands"))
    registered_agents = _normalize_path_list(manifest.get("agents"))
    skills_declared = manifest.get("skills") is not None
    hooks_declared = manifest.get("hooks") is not None
    styles_declared = manifest.get("outputStyles") is not None
    providers_declared = manifest.get("providers") is not None
    schemas_declared = manifest.get("schemas") is not None
    lsp_declared = manifest.get("lspServers") is not None

    commands = _collect_markdown_assets(
        root,
        "commands",
        registered_commands,
        kind="command",
    )
    agents = _collect_markdown_assets(
        root,
        "agents",
        registered_agents,
        kind="agent",
    )
    skills = _collect_skills(root, skills_declared)
    scripts = _collect_scripts(root)
    hooks = _collect_hooks(root, hooks_declared)
    templates = _collect_templates(
        root,
        styles_declared=styles_declared,
        providers_declared=providers_declared,
        schemas_declared=schemas_declared,
    )

    settings_path = root / "settings.json"
    lsp_path = root / ".claude-plugin" / ".lsp.json"
    has_settings = settings_path.is_file()
    has_lsp = lsp_declared or (lsp_path.is_file() and lsp_path.stat().st_size > 2)

    metadata: dict[str, Any] = {
        "manifest": manifest,
        "manifest_path": str(manifest_path),
        "registered_commands": sorted(registered_commands),
        "registered_agents": sorted(registered_agents),
        "skills_declared": skills_declared,
        "hooks_declared": hooks_declared,
        "has_settings": has_settings,
        "has_lsp": has_lsp,
        "registration_gaps": _registration_gaps(
            commands, agents, hooks, hooks_declared
        ),
    }

    definition = PluginDefinition(
        id=name,
        name=name,
        version=version,
        root=str(root),
        description=description,
        source_hash=_hash_plugin_tree(root),
        commands=tuple(commands),
        agents=tuple(agents),
        skills=tuple(skills),
        scripts=tuple(scripts),
        hooks=tuple(hooks),
        templates=tuple(templates),
        metadata=metadata,
    )

    capabilities = detect_capabilities(definition)
    constructs = _union_constructs(definition)

    return PluginDefinition(
        id=definition.id,
        name=definition.name,
        version=definition.version,
        root=definition.root,
        description=definition.description,
        source_hash=definition.source_hash,
        commands=definition.commands,
        agents=definition.agents,
        skills=definition.skills,
        scripts=definition.scripts,
        hooks=definition.hooks,
        templates=definition.templates,
        capabilities=capabilities,
        constructs=constructs,
        metadata=definition.metadata,
    )


def _normalize_path_list(value: Any) -> set[str]:
    if value is None:
        return set()
    if isinstance(value, str):
        items = [value]
    elif isinstance(value, list):
        items = [str(v) for v in value]
    else:
        return set()
    normalized: set[str] = set()
    for item in items:
        rel = item.replace("\\", "/").lstrip("./")
        normalized.add(rel)
    return normalized


def _parse_frontmatter(text: str) -> tuple[Frontmatter, str]:
    match = _FRONTMATTER_RE.match(text)
    if not match:
        return Frontmatter(), text

    raw_block, body = match.group(1), match.group(2)
    data = _parse_simple_yaml(raw_block)
    tools_raw = data.get("tools")
    tools: tuple[str, ...] = ()
    if isinstance(tools_raw, str):
        tools = tuple(t.strip() for t in tools_raw.split(",") if t.strip())
    elif isinstance(tools_raw, list):
        tools = tuple(str(t).strip() for t in tools_raw if str(t).strip())

    known = {
        "name",
        "description",
        "tools",
        "model",
        "disable-model-invocation",
        "argument-hint",
    }
    extra = {k: v for k, v in data.items() if k not in known}
    frontmatter = Frontmatter(
        raw=data,
        name=str(data["name"]) if data.get("name") is not None else None,
        description=(
            str(data["description"]) if data.get("description") is not None else None
        ),
        tools=tools,
        model=str(data["model"]) if data.get("model") is not None else None,
        disable_model_invocation=bool(data.get("disable-model-invocation")),
        extra=extra,
    )
    return frontmatter, body


def _parse_simple_yaml(block: str) -> dict[str, Any]:
    """Minimal frontmatter parser (key: value lines). Enough for plugin assets."""
    result: dict[str, Any] = {}
    for line in block.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if ":" not in stripped:
            continue
        key, value = stripped.split(":", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if value.lower() in ("true", "yes"):
            result[key] = True
        elif value.lower() in ("false", "no"):
            result[key] = False
        else:
            result[key] = value
    return result


def _collect_markdown_assets(
    root: Path,
    dirname: str,
    registered: set[str],
    *,
    kind: str,
) -> list[Any]:
    directory = root / dirname
    if not directory.is_dir():
        return []

    items: list[Any] = []
    for path in sorted(directory.rglob("*.md")):
        if path.name.upper() == "README.MD":
            continue
        rel = path.relative_to(root).as_posix()
        text = path.read_text(encoding="utf-8-sig")
        frontmatter, body = _parse_frontmatter(text)
        constructs = detect_constructs_in_text(text)
        asset_id = frontmatter.name or path.stem
        if registered:
            is_registered = rel in registered
        else:
            # No manifest list — treat on-disk assets as discoverable.
            is_registered = True
        cls = CommandDefinition if kind == "command" else AgentDefinition
        items.append(
            cls(
                id=asset_id,
                path=rel,
                body=body,
                frontmatter=frontmatter,
                constructs=constructs,
                registered=is_registered,
            )
        )
    return items


def _collect_skills(root: Path, skills_declared: bool) -> list[SkillDefinition]:
    skills_dir = root / "skills"
    if not skills_dir.is_dir():
        return []

    skills: list[SkillDefinition] = []
    for skill_md in sorted(skills_dir.rglob("SKILL.md")):
        rel = skill_md.relative_to(root).as_posix()
        text = skill_md.read_text(encoding="utf-8-sig")
        frontmatter, body = _parse_frontmatter(text)
        constructs = detect_constructs_in_text(text)
        skill_id = frontmatter.name or skill_md.parent.name
        skills.append(
            SkillDefinition(
                id=skill_id,
                path=rel,
                body=body,
                frontmatter=frontmatter,
                constructs=constructs,
                registered=skills_declared,
            )
        )
    return skills


def _collect_scripts(root: Path) -> list[ScriptDefinition]:
    scripts_dir = root / "scripts"
    if not scripts_dir.is_dir():
        return []

    allowed = {".sh", ".py", ".js", ".ts", ".ps1"}
    scripts: list[ScriptDefinition] = []
    for path in sorted(scripts_dir.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in allowed:
            continue
        rel = path.relative_to(root).as_posix()
        scripts.append(
            ScriptDefinition(
                id=path.stem,
                path=rel,
                relative_path=rel,
            )
        )
    return scripts


def _collect_hooks(root: Path, hooks_declared: bool) -> list[HookDefinition]:
    hooks_json = root / "hooks" / "hooks.json"
    if not hooks_json.is_file():
        return []

    try:
        data = json.loads(hooks_json.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError:
        return []

    hooks_block = data.get("hooks") if isinstance(data, dict) else None
    if not isinstance(hooks_block, dict):
        return []

    results: list[HookDefinition] = []
    for event, entries in hooks_block.items():
        constructs: list[ClaudeConstruct] = []
        if event == "PreToolUse":
            constructs.append(ClaudeConstruct.PRE_TOOL_USE)
        elif event == "PostToolUse":
            constructs.append(ClaudeConstruct.POST_TOOL_USE)
        elif event == "SessionStart":
            constructs.append(ClaudeConstruct.SESSION_START)

        if not isinstance(entries, list):
            continue
        for entry in entries:
            if not isinstance(entry, dict):
                continue
            matcher = entry.get("matcher")
            nested = entry.get("hooks")
            if not isinstance(nested, list):
                continue
            for hook in nested:
                if not isinstance(hook, dict):
                    continue
                command = hook.get("command")
                command_str = str(command) if command is not None else None
                script_path = _extract_script_path(command_str) if command_str else None
                results.append(
                    HookDefinition(
                        event=str(event),
                        matcher=str(matcher) if matcher is not None else None,
                        command=command_str,
                        script_path=script_path,
                        constructs=tuple(constructs),
                        registered=hooks_declared,
                    )
                )
    return results


def _extract_script_path(command: str) -> str | None:
    match = _CLAUDE_ROOT_IN_CMD.search(command)
    if not match:
        return None
    return match.group(1) or match.group(2)


def _collect_templates(
    root: Path,
    *,
    styles_declared: bool,
    providers_declared: bool,
    schemas_declared: bool,
) -> list[TemplateDefinition]:
    templates: list[TemplateDefinition] = []
    for dirname, kind, _declared in (
        ("styles", "styles", styles_declared),
        ("providers", "providers", providers_declared),
        ("schemas", "schemas", schemas_declared),
    ):
        directory = root / dirname
        if not directory.is_dir():
            continue
        for path in sorted(directory.rglob("*")):
            if not path.is_file():
                continue
            if path.name.startswith("."):
                continue
            rel = path.relative_to(root).as_posix()
            templates.append(
                TemplateDefinition(
                    id=path.stem,
                    path=rel,
                    kind=kind,
                )
            )
    return templates


def _registration_gaps(
    commands: list[CommandDefinition],
    agents: list[AgentDefinition],
    hooks: list[HookDefinition],
    hooks_declared: bool,
) -> dict[str, list[str]]:
    return {
        "unregistered_commands": [c.path for c in commands if not c.registered],
        "unregistered_agents": [a.path for a in agents if not a.registered],
        "hooks_on_disk_unregistered": (
            [h.event for h in hooks] if hooks and not hooks_declared else []
        ),
    }


def _union_constructs(definition: PluginDefinition) -> tuple[ClaudeConstruct, ...]:
    found: set[ClaudeConstruct] = set()
    for cmd in definition.commands:
        found.update(cmd.constructs)
    for agent in definition.agents:
        found.update(agent.constructs)
    for skill in definition.skills:
        found.update(skill.constructs)
    for hook in definition.hooks:
        found.update(hook.constructs)
    return tuple(sorted(found, key=lambda c: c.value))


def _hash_plugin_tree(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        # Skip large/binary noise; hash path + size + mtime for speed
        rel = path.relative_to(root).as_posix()
        if any(part.startswith(".") and part not in {".claude-plugin"} for part in path.parts):
            # still include .claude-plugin
            pass
        try:
            stat = path.stat()
        except OSError:
            continue
        digest.update(rel.encode("utf-8"))
        digest.update(str(stat.st_size).encode("utf-8"))
        digest.update(str(int(stat.st_mtime)).encode("utf-8"))
    return digest.hexdigest()[:16]

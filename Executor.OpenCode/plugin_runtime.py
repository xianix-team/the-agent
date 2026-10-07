"""OpenCode plugin overlay — convert Claude markdown to OpenCode format at runtime.

No fat bundle, no VERSION/COMPATIBILITY side files. Shared scripts/providers stay
at the plugin root; only converted .md files are written under WORK_DIR/.opencode/.
"""

from __future__ import annotations

import json
import os
import re
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

DEFAULT_PLUGINS_DIR = "/workspace/vendor/plugins"

_OPENCODE_NOTE = (
    "> **OpenCode runtime note:** Claude `Task` / `Agent` tool orchestration is not "
    "available. Invoke specialist agents with the OpenCode `task` tool and "
    "`subagent_type` set to the agent name (or `@agent-name`). "
    "Use `CLAUDE_PLUGIN_ROOT` for scripts (the executor sets it to this plugin root).\n\n"
)

# OpenCode skill descriptions are capped; truncate with an ellipsis budget.
MAX_SKILL_DESCRIPTION_LENGTH = 1024

_SUBAGENT_JSON_RE = re.compile(
    r'"subagent_type"\s*:\s*"([^"]+)"',
    re.IGNORECASE,
)

_TOOL_TO_PERMISSION = {
    "read": ("read", "allow"),
    "grep": ("grep", "allow"),
    "glob": ("glob", "allow"),
    "bash": ("bash", "allow"),
    "write": ("edit", "allow"),
    "edit": ("edit", "allow"),
    "task": ("task", "allow"),
    "agent": ("task", "allow"),
}


@dataclass
class PluginBundle:
    """Plugin whose Claude markdown was converted for OpenCode."""

    plugin_id: str
    plugin_version: str
    plugin_root: Path
    overlay_dir: Path | None = None
    commands: list[str] = field(default_factory=list)
    agents: list[str] = field(default_factory=list)
    skills: list[str] = field(default_factory=list)
    lead_command_path: str | None = None

    def version_info(self) -> dict[str, Any]:
        return {
            "harness": "opencode",
            "plugin": self.plugin_id,
            "plugin_version": self.plugin_version,
            "commands": list(self.commands),
            "agents": list(self.agents),
            "skills": list(self.skills),
            "plugin_root": str(self.plugin_root),
            "overlay_dir": str(self.overlay_dir) if self.overlay_dir else None,
            "lead_command_path": self.lead_command_path,
            "source": "claude-md-convert",
        }


def discover_plugin_roots(plugins_dir: str | None = None) -> list[Path]:
    """List plugin directories that have Claude sources (or a legacy .opencode/ tree)."""
    root = Path(
        plugins_dir
        or os.environ.get("XIANIX_PLUGINS_DIR")
        or DEFAULT_PLUGINS_DIR
    )
    if not root.is_dir():
        return []
    found: list[Path] = []
    for child in sorted(root.iterdir()):
        if not child.is_dir():
            continue
        if (child / ".claude-plugin" / "plugin.json").is_file() or (
            child / "commands"
        ).is_dir():
            found.append(child)
            continue
        if (child / ".opencode").is_dir():
            found.append(child)
    return found


def resolve_requested_plugins() -> list[str]:
    """Plugin names from CLAUDE_CODE_PLUGINS JSON, if present."""
    raw = os.environ.get("CLAUDE_CODE_PLUGINS") or os.environ.get("CLAUDE-CODE-PLUGINS")
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return []
    names: list[str] = []
    if isinstance(data, list):
        for item in data:
            if not isinstance(item, dict):
                continue
            name = str(item.get("plugin-name") or item.get("name") or "")
            name = name.split("@", 1)[0].strip()
            if name:
                names.append(name)
    return names


def match_slash_command(prompt: str, command_ids: list[str]) -> str | None:
    """Return the first command id whose /name appears in the prompt."""
    if not prompt or not command_ids:
        return None
    for cmd in command_ids:
        if re.search(rf"(?i)/(?:{re.escape(cmd)})\b", prompt):
            return cmd
    return None


def _plugin_version(plugin_root: Path) -> str:
    manifest = plugin_root / ".claude-plugin" / "plugin.json"
    if not manifest.is_file():
        return "unknown"
    try:
        data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return "unknown"
    return str(data.get("version") or "unknown")


def _split_frontmatter(text: str) -> tuple[dict[str, Any], str]:
    if not text.startswith("---"):
        return {}, text
    end = text.find("\n---", 3)
    if end == -1:
        return {}, text
    raw_fm = text[3:end].strip("\n")
    body = text[end + 4 :]
    if body.startswith("\n"):
        body = body[1:]
    data: dict[str, Any] = {}
    for line in raw_fm.splitlines():
        if not line.strip() or line.lstrip().startswith("#") or ":" not in line:
            continue
        key, _, value = line.partition(":")
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            data[key] = value
    return data, body


def _render_frontmatter(data: dict[str, Any]) -> str:
    if not data:
        return ""
    lines = ["---"]
    for key, value in data.items():
        if isinstance(value, dict):
            lines.append(f"{key}:")
            for nested_key, nested_val in value.items():
                lines.append(f"  {nested_key}: {nested_val}")
            continue
        text = str(value)
        if any(c in text for c in (":", "#", '"', "'")) or "\n" in text:
            escaped = text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ")
            lines.append(f'{key}: "{escaped}"')
        else:
            lines.append(f"{key}: {text}")
    lines.append("---")
    lines.append("")
    return "\n".join(lines)


def _permissions_from_tools(tools_raw: str) -> dict[str, str]:
    perms: dict[str, str] = {}
    for part in re.split(r"[, ]+", tools_raw):
        key = part.strip().lower()
        if not key:
            continue
        mapped = _TOOL_TO_PERMISSION.get(key)
        if mapped:
            perm_key, action = mapped
            perms[perm_key] = action
    return perms


def _normalize_skill_name(raw: str) -> str:
    name = raw.strip().lower().replace("_", "-").replace(" ", "-")
    name = re.sub(r"[^a-z0-9-]", "", name)
    name = re.sub(r"-{2,}", "-", name).strip("-")
    return (name or "skill")[:64]


def _rewrite_subagent_refs(body: str) -> str:
    """Map Claude subagent_type JSON keys to an OpenCode-friendly agent hint.

    Does not rewrite English words like \"task\" or .NET Task.WhenAll.
    """

    def _repl(match: re.Match[str]) -> str:
        agent = match.group(1)
        return (
            f'"agent": "{agent}"  '
            f'/* OpenCode: task/subagent_type="{agent}" or @{agent} */'
        )

    return _SUBAGENT_JSON_RE.sub(_repl, body)


def convert_command_markdown(text: str) -> str:
    """Convert Claude command markdown to OpenCode format.

    Keeps a description frontmatter field when present, rewrites Claude
    ``subagent_type`` JSON keys to OpenCode agent hints, and prepends the
    OpenCode runtime note when missing.
    """
    fm, body = _split_frontmatter(text)
    out_fm: dict[str, Any] = {}
    if fm.get("description"):
        out_fm["description"] = fm["description"]
    body = _rewrite_subagent_refs(body)
    if "OpenCode runtime note" not in body:
        body = _OPENCODE_NOTE + body.lstrip("\n")
    return _render_frontmatter(out_fm) + body.rstrip() + "\n"


def convert_agent_markdown(text: str) -> str:
    """Convert Claude agent markdown to an OpenCode subagent definition.

    Forces ``mode: subagent``, maps Claude tool lists to OpenCode
    ``permission`` entries, and rewrites ``subagent_type`` references.
    """
    fm, body = _split_frontmatter(text)
    out_fm: dict[str, Any] = {"mode": "subagent"}
    if fm.get("description"):
        out_fm["description"] = fm["description"]
    elif fm.get("name"):
        out_fm["description"] = f"Specialist agent: {fm['name']}"
    else:
        out_fm["description"] = "Specialist agent"
    tools = fm.get("tools") or ""
    perms = _permissions_from_tools(str(tools))
    if perms:
        out_fm["permission"] = perms
    body = _rewrite_subagent_refs(body)
    return _render_frontmatter(out_fm) + body.rstrip() + "\n"


def convert_skill_markdown(text: str, skill_id: str) -> str:
    """Convert Claude skill markdown to OpenCode skill format.

    Normalizes the skill name, caps description length, sets
    ``compatibility: opencode``, rewrites subagent refs, and prepends the
    OpenCode runtime note when missing.
    """
    fm, body = _split_frontmatter(text)
    name = _normalize_skill_name(str(fm.get("name") or skill_id))
    description = str(fm.get("description") or f"Skill: {name}").strip()
    if len(description) > MAX_SKILL_DESCRIPTION_LENGTH:
        ellipsis_budget = MAX_SKILL_DESCRIPTION_LENGTH - 3
        description = description[:ellipsis_budget] + "..."
    out_fm = {
        "name": name,
        "description": description,
        "compatibility": "opencode",
    }
    body = _rewrite_subagent_refs(body)
    if "OpenCode runtime note" not in body:
        body = _OPENCODE_NOTE + body.lstrip("\n")
    return _render_frontmatter(out_fm) + body.rstrip() + "\n"


def _reset_plugin_subdir(path: Path) -> None:
    """Replace a plugin recipe subdir without wiping sibling overlay files."""
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def _convert_plugin_to_overlay(plugin_root: Path, overlay: Path) -> PluginBundle:
    """Convert Claude markdown into overlay/.opencode/{commands,agents,skills}.

    Merges into an existing worktree ``.opencode`` tree when present: only the
    plugin ``commands`` / ``agents`` / ``skills`` subtrees are replaced. Other
    project-owned files (e.g. ``opencode.json``) are preserved.
    """
    overlay.mkdir(parents=True, exist_ok=True)
    commands_dir = overlay / "commands"
    agents_dir = overlay / "agents"
    skills_dir = overlay / "skills"

    commands: list[str] = []
    src_commands = plugin_root / "commands"
    if src_commands.is_dir():
        _reset_plugin_subdir(commands_dir)
        for path in sorted(src_commands.glob("*.md")):
            converted = convert_command_markdown(path.read_text(encoding="utf-8"))
            (commands_dir / path.name).write_text(converted, encoding="utf-8")
            commands.append(path.stem)

    agents: list[str] = []
    src_agents = plugin_root / "agents"
    if src_agents.is_dir():
        _reset_plugin_subdir(agents_dir)
        for path in sorted(src_agents.glob("*.md")):
            converted = convert_agent_markdown(path.read_text(encoding="utf-8"))
            # Prefer stem without trailing -agent for OpenCode agent ids when present.
            stem = path.stem
            agent_id = stem.removesuffix("-agent") if stem.endswith("-agent") else stem
            dest_name = f"{agent_id}.md"
            (agents_dir / dest_name).write_text(converted, encoding="utf-8")
            agents.append(agent_id)

    skills: list[str] = []
    src_skills = plugin_root / "skills"
    if src_skills.is_dir():
        _reset_plugin_subdir(skills_dir)
        for skill_dir in sorted(src_skills.iterdir()):
            skill_md = skill_dir / "SKILL.md"
            if not skill_dir.is_dir() or not skill_md.is_file():
                continue
            converted = convert_skill_markdown(
                skill_md.read_text(encoding="utf-8"),
                skill_dir.name,
            )
            dest = skills_dir / skill_dir.name
            dest.mkdir(parents=True, exist_ok=True)
            (dest / "SKILL.md").write_text(converted, encoding="utf-8")
            skills.append(skill_dir.name)

    if not commands and not agents and not skills:
        # Fallback: merge legacy committed .opencode/ if Claude sources are absent.
        legacy = plugin_root / ".opencode"
        if legacy.is_dir():
            for name in ("commands", "agents", "skills"):
                src = legacy / name
                if not src.is_dir():
                    continue
                dest = overlay / name
                if dest.exists():
                    shutil.rmtree(dest)
                shutil.copytree(src, dest)
            commands = (
                sorted(p.stem for p in (overlay / "commands").glob("*.md"))
                if (overlay / "commands").is_dir()
                else []
            )
            agents = (
                sorted(p.stem for p in (overlay / "agents").glob("*.md"))
                if (overlay / "agents").is_dir()
                else []
            )
            if (overlay / "skills").is_dir():
                skills = [
                    child.name
                    for child in sorted((overlay / "skills").iterdir())
                    if child.is_dir() and (child / "SKILL.md").is_file()
                ]

    if not commands and not agents and not skills:
        raise RuntimeError(
            f"Plugin '{plugin_root.name}' has no Claude commands/agents/skills "
            "to convert for OpenCode"
        )

    lead = None
    if commands:
        candidate = commands_dir / f"{commands[0]}.md"
        if candidate.is_file():
            lead = str(candidate.resolve())

    return PluginBundle(
        plugin_id=plugin_root.name,
        plugin_version=_plugin_version(plugin_root),
        plugin_root=plugin_root.resolve(),
        overlay_dir=overlay.resolve(),
        commands=commands,
        agents=agents,
        skills=skills,
        lead_command_path=lead,
    )


def _list_claude_command_ids(plugin_root: Path) -> list[str]:
    src = plugin_root / "commands"
    if src.is_dir():
        return sorted(p.stem for p in src.glob("*.md"))
    legacy = plugin_root / ".opencode" / "commands"
    if legacy.is_dir():
        return sorted(p.stem for p in legacy.glob("*.md"))
    return []


def prepare_plugins_for_prompt(
    prompt: str,
    *,
    work_dir: str | None = None,
    plugins_dir: str | None = None,
) -> dict[str, Any] | None:
    """Convert Claude plugin markdown and install OpenCode overlay when matched."""
    requested = set(resolve_requested_plugins())
    if not requested:
        return None

    roots = discover_plugin_roots(plugins_dir)
    if not roots:
        plugins_root = (
            plugins_dir
            or os.environ.get("XIANIX_PLUGINS_DIR")
            or DEFAULT_PLUGINS_DIR
        )
        raise RuntimeError(
            f"use-plugins requested {', '.join(sorted(requested))} "
            f"but no plugins found under {plugins_root}"
        )

    matched_roots = [r for r in roots if r.name in requested]
    if not matched_roots:
        available = ", ".join(r.name for r in roots) or "(none)"
        raise RuntimeError(
            f"Requested plugins not published in executor vendor: "
            f"{', '.join(sorted(requested))}. Available: {available}"
        )

    if not work_dir:
        raise RuntimeError("WORK_DIR is required to convert plugin markdown for OpenCode")

    work = Path(work_dir)
    work.mkdir(parents=True, exist_ok=True)

    all_commands: list[str] = []
    command_to_root: dict[str, Path] = {}
    for root in matched_roots:
        for cmd in _list_claude_command_ids(root):
            all_commands.append(cmd)
            command_to_root.setdefault(cmd, root)

    matched = match_slash_command(prompt, all_commands)
    primary_root = (
        command_to_root.get(matched) if matched else matched_roots[0]
    )

    overlay = work / ".opencode"
    primary = _convert_plugin_to_overlay(primary_root, overlay)

    os.environ["CLAUDE_PLUGIN_ROOT"] = str(primary.plugin_root)
    os.environ["XIANIX_PLUGIN_ROOT"] = str(primary.plugin_root)
    os.environ["XIANIX_PLUGIN_ID"] = primary.plugin_id
    # Clear prior match state so free-form prompts with use-plugins do not
    # inherit a stale matched command / lead path from an earlier run.
    os.environ.pop("XIANIX_MATCHED_COMMAND", None)
    os.environ.pop("XIANIX_LEAD_COMMAND_PATH", None)

    lead = None
    if matched and primary.overlay_dir:
        candidate = primary.overlay_dir / "commands" / f"{matched}.md"
        if candidate.is_file():
            lead = str(candidate.resolve())
            primary.lead_command_path = lead
            os.environ["XIANIX_MATCHED_COMMAND"] = matched
            os.environ["XIANIX_LEAD_COMMAND_PATH"] = lead

    version = primary.version_info()
    info: dict[str, Any] = {
        "harness": "opencode",
        "matched_command": matched,
        "plugins": [version],
    }
    info.update(version)
    return info


def format_version_logs(info: dict[str, Any], model: str | None = None) -> list[str]:
    lines = [
        f"Harness: {info.get('harness', 'opencode')}",
        f"Model: {model or '(opencode default)'}",
    ]
    if info.get("plugin"):
        lines.append(f"Plugin: {info.get('plugin')}")
        lines.append(f"Plugin version: {info.get('plugin_version')}")
        lines.append(f"Plugin root: {info.get('plugin_root')}")
        if info.get("source"):
            lines.append(f"Plugin source: {info.get('source')}")
    if info.get("matched_command"):
        lines.append(f"Matched command: /{info['matched_command']}")
    return lines


def prepare_plugins_or_raise(prompt: str, **_kwargs: Any) -> dict[str, Any] | None:
    """Convert and install OpenCode overlays; fail closed when plugins are missing."""
    work_dir = os.environ.get("WORK_DIR") or os.environ.get("WORKDIR")
    info = prepare_plugins_for_prompt(prompt, work_dir=work_dir)
    if info is None:
        return None
    return info

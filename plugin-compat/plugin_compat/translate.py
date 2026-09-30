"""Translate Claude-oriented markdown into OpenCode-native forms.

Transformations are construct-based — never plugin-name specific.
"""

from __future__ import annotations

import re
from typing import Any

from .model import Frontmatter

# Claude Task/Agent JSON blobs that name a subagent_type.
_SUBAGENT_JSON_RE = re.compile(
    r'"subagent_type"\s*:\s*"([^"]+)"',
    re.IGNORECASE,
)
_MODEL_SLUG_RE = re.compile(
    r'("model"\s*:\s*")(?:haiku|sonnet|opus|fable)(")',
    re.IGNORECASE,
)
_MODEL_FM_RE = re.compile(
    r"(?m)^model:\s*(haiku|sonnet|opus|fable|inherit)\s*$",
    re.IGNORECASE,
)
_TASK_TOOL_HINT_RE = re.compile(
    r"\b(?:Task|Agent)\s*/\s*`?(?:Task|Agent)`?|"
    r"\bTask\b(?:\s*/\s*\bAgent\b)?|"
    r"the `Task`(?:\s*/\s*`Agent`)? tool|"
    r"`Task`(?:\s*/\s*`Agent`)?",
    re.IGNORECASE,
)

_CLAUDE_ONLY_FM_KEYS = frozenset(
    {
        "tools",
        "disable-model-invocation",
        "argument-hint",
        "color",
    }
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


def translate_command_frontmatter(fm: Frontmatter) -> dict[str, Any]:
    """OpenCode command frontmatter (description only is required)."""
    out: dict[str, Any] = {}
    if fm.description:
        out["description"] = fm.description
    # Do not pass Claude model slugs or tools through.
    return out


def translate_agent_frontmatter(fm: Frontmatter) -> dict[str, Any]:
    """OpenCode agent frontmatter with mode: subagent."""
    out: dict[str, Any] = {
        "mode": "subagent",
    }
    if fm.description:
        out["description"] = fm.description
    elif fm.name:
        out["description"] = f"Specialist agent: {fm.name}"
    else:
        out["description"] = "Specialist agent"

    permissions = _permissions_from_tools(fm.tools)
    if permissions:
        out["permission"] = permissions
    return out


def translate_skill_frontmatter(fm: Frontmatter, skill_id: str) -> dict[str, Any]:
    """OpenCode skill frontmatter (name + description required)."""
    name = _normalize_skill_name(fm.name or skill_id)
    description = (fm.description or f"Skill: {name}").strip()
    if len(description) > 1024:
        description = description[:1021] + "..."
    return {
        "name": name,
        "description": description,
        "compatibility": "opencode",
    }


def translate_body(body: str, *, kind: str) -> str:
    """Rewrite Claude constructs in markdown bodies for OpenCode."""
    text = body
    text = _MODEL_SLUG_RE.sub(r"\1inherit\2", text)
    text = _MODEL_FM_RE.sub("", text)

    # Point subagent_type usages at OpenCode @agent invocation.
    def _subagent_repl(match: re.Match[str]) -> str:
        agent = match.group(1)
        return f'"agent": "{agent}"  /* OpenCode: invoke @{agent} or Task with this agent */'

    text = _SUBAGENT_JSON_RE.sub(_subagent_repl, text)

    if kind in ("command", "skill"):
        preamble = (
            "\n> **OpenCode runtime note:** Claude `Task`/`subagent_type` orchestration "
            "is not available. Invoke specialist agents with `@agent-name` (or the "
            "OpenCode Task tool naming that agent). Use `CLAUDE_PLUGIN_ROOT` for scripts "
            "(set by the executor to this plugin bundle root).\n\n"
        )
        if "OpenCode runtime note" not in text:
            text = preamble + text

    text = _TASK_TOOL_HINT_RE.sub("OpenCode Task/@agent", text)
    return text.strip() + "\n"


def render_frontmatter(data: dict[str, Any]) -> str:
    """Render a small YAML frontmatter block."""
    if not data:
        return ""
    lines = ["---"]
    for key, value in data.items():
        if isinstance(value, dict):
            lines.append(f"{key}:")
            for nested_key, nested_val in value.items():
                lines.append(f"  {nested_key}: {nested_val}")
        elif isinstance(value, bool):
            lines.append(f"{key}: {'true' if value else 'false'}")
        else:
            # Quote if needed
            text = str(value)
            if ":" in text or text.startswith("#") or "\n" in text:
                text = json_escape(text)
                lines.append(f'{key}: "{text}"')
            else:
                lines.append(f"{key}: {text}")
    lines.append("---")
    lines.append("")
    return "\n".join(lines)


def json_escape(text: str) -> str:
    return text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ")


def _permissions_from_tools(tools: tuple[str, ...]) -> dict[str, str]:
    perms: dict[str, str] = {}
    for tool in tools:
        key = tool.strip().lower()
        mapped = _TOOL_TO_PERMISSION.get(key)
        if mapped:
            perm_key, action = mapped
            perms[perm_key] = action
    return perms


def _normalize_skill_name(raw: str) -> str:
    name = raw.strip().lower().replace("_", "-").replace(" ", "-")
    name = re.sub(r"[^a-z0-9-]", "", name)
    name = re.sub(r"-{2,}", "-", name).strip("-")
    if not name:
        name = "skill"
    return name[:64]

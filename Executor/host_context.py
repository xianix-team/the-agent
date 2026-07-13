"""Prepend structural host context to Claude Code prompts.

When the control plane declares ``platform`` in ``XIANIX_INPUTS``, free-form
prompts (e.g. PR comment instructions) often omit the hosting service. Env vars
alone are easy for Claude Code to miss; a short prompt preamble makes GitHub vs
Azure DevOps tool choice explicit. Detection from ``git remote`` remains
authoritative inside plugins — this block is a hint, not a substitute.
"""
from __future__ import annotations

import json

HOST_CONTEXT_MARKER = "[Xianix host context]"

_PLATFORM_HINT = (
    "Use this hosting platform for any PR/issue/comment API calls "
    "(GitHub → gh; Azure DevOps → Azure DevOps REST / providers/azure-devops.md — not gh). "
    "Confirm with `git remote get-url origin` if needed."
)


def parse_inputs(inputs_raw: str | None) -> dict:
    """Parse ``XIANIX_INPUTS`` JSON; return {} on missing/invalid input."""
    if not inputs_raw or not inputs_raw.strip():
        return {}
    try:
        data = json.loads(inputs_raw)
    except json.JSONDecodeError:
        return {}
    return data if isinstance(data, dict) else {}


def build_host_context_block(platform: str, repository_name: str = "") -> str:
    """Build the preamble block for a non-empty platform value."""
    lines = [HOST_CONTEXT_MARKER, f"platform: {platform}"]
    if repository_name:
        lines.append(f"repository-name: {repository_name}")
    lines.append("")
    lines.append(_PLATFORM_HINT)
    lines.append("")
    lines.append("---")
    # Trailing blank line so the original prompt starts on its own paragraph.
    return "\n".join(lines) + "\n\n"


def prepend_host_context(prompt: str, inputs_raw: str | None) -> str:
    """Prepend host context when ``platform`` is set; otherwise return ``prompt`` unchanged.

    Idempotent: if ``prompt`` already starts with the host-context marker, return it as-is.
    Does not invent a platform when the key is absent or empty.
    """
    if not prompt:
        return prompt

    stripped = prompt.lstrip()
    if stripped.startswith(HOST_CONTEXT_MARKER):
        return prompt

    inputs = parse_inputs(inputs_raw)
    platform = str(inputs.get("platform") or "").strip()
    if not platform:
        return prompt

    repo_name = str(inputs.get("repository-name") or "").strip()
    return build_host_context_block(platform, repo_name) + prompt

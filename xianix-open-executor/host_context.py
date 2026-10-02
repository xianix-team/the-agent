"""Prepend structural host context to OpenCode prompts.

When the control plane declares ``platform`` in ``XIANIX_INPUTS``, free-form
prompts often omit the hosting service. A short preamble makes GitHub vs Azure
DevOps tool choice explicit.

Also surfaces provisioned mise runtimes when present.
"""
from __future__ import annotations

import json

HOST_CONTEXT_MARKER = "[Xianix host context]"

_PLATFORM_TOOL_HINTS = {
    "github": "use the `gh` CLI",
    "azuredevops": "use the Azure DevOps REST API (see providers/azure-devops.md), not `gh`",
}
_PLATFORM_TOOL_HINT_FALLBACK = "use this platform's native API/CLI"


def _platform_line(platform: str) -> str:
    tool = _PLATFORM_TOOL_HINTS.get(platform.strip().lower(), _PLATFORM_TOOL_HINT_FALLBACK)
    return (
        f"platform: {platform} — for PR/issue/comment API calls {tool} "
        "(confirm with `git remote get-url origin`)"
    )


def parse_inputs(inputs_raw: str | None) -> dict:
    if not inputs_raw or not inputs_raw.strip():
        return {}
    try:
        data = json.loads(inputs_raw)
    except json.JSONDecodeError:
        return {}
    return data if isinstance(data, dict) else {}


def build_host_context_block(
    platform: str,
    repository_name: str = "",
    runtimes: str = "",
) -> str:
    lines = [HOST_CONTEXT_MARKER]

    if platform:
        lines.append(_platform_line(platform))
        if repository_name:
            lines.append(f"repository-name: {repository_name}")

    if runtimes:
        lines.append(
            f"provisioned runtimes (already installed and on PATH): {runtimes}"
        )

    lines.append("")
    lines.append("---")
    return "\n".join(lines) + "\n\n"


def prepend_host_context(
    prompt: str,
    inputs_raw: str | None,
    runtimes: str | None = None,
) -> str:
    if not prompt:
        return prompt

    if HOST_CONTEXT_MARKER in prompt:
        return prompt

    inputs = parse_inputs(inputs_raw)
    platform = str(inputs.get("platform") or "").strip()
    runtimes_str = str(runtimes or "").strip()

    if not platform and not runtimes_str:
        return prompt

    repo_name = str(inputs.get("repository-name") or "").strip()
    return build_host_context_block(platform, repo_name, runtimes_str) + prompt

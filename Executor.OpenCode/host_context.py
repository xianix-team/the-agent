"""Prepend structural host context to OpenCode prompts.

When the control plane declares ``platform`` in ``XIANIX_INPUTS``, free-form
prompts often omit the hosting service. A short preamble makes GitHub vs Azure
DevOps tool choice explicit.

Also surfaces provisioned mise runtimes and, when a harness-native OpenCode
plugin slash-command is selected, injects a pointer to the lead command.
"""
from __future__ import annotations

import json
import os

HOST_CONTEXT_MARKER = "[Xianix host context]"
RECIPE_CONTEXT_MARKER = "[Xianix OpenCode recipe]"

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


def build_plugin_recipe_block(prompt: str) -> str | None:
    """Inject OpenCode lead-command guidance when a slash command matched.

    Requires ``XIANIX_MATCHED_COMMAND`` and ``XIANIX_LEAD_COMMAND_PATH`` from
    ``plugin_runtime.prepare_plugins_for_prompt``. Free-form prompts that only
    list ``use-plugins`` (e.g. @xianix comment replies) must NOT get the lead
    recipe forced on them.
    """
    if not prompt or RECIPE_CONTEXT_MARKER in prompt:
        return None

    matched = os.environ.get("XIANIX_MATCHED_COMMAND", "").strip()
    if not matched:
        return None

    lead = os.environ.get("XIANIX_LEAD_COMMAND_PATH", "").strip()
    if not lead or not os.path.isfile(lead):
        return None

    plugin_root = os.environ.get(
        "XIANIX_PLUGIN_ROOT",
        os.environ.get("CLAUDE_PLUGIN_ROOT", ""),
    )
    plugin_id = os.environ.get("XIANIX_PLUGIN_ID", "plugin")

    return (
        f"{RECIPE_CONTEXT_MARKER}\n"
        f"This is an OpenCode executor — plugins are installed with "
        f"`claude plugin marketplace add/install` (same as the Claude executor), "
        f"then converted to a harness-native `.opencode/` recipe. "
        f"Plugin `{plugin_id}` is staged under CLAUDE_PLUGIN_ROOT.\n"
        f"Matched command: /{matched}. Read and follow {lead} exactly.\n"
        f"Use scripts under CLAUDE_PLUGIN_ROOT={plugin_root} "
        f"(also exported as XIANIX_PLUGIN_ROOT). Do not invent a shallow "
        "summary-only run when the command requires posting platform results.\n"
        "\n---\n\n"
    )


def prepend_host_context(
    prompt: str,
    inputs_raw: str | None,
    runtimes: str | None = None,
) -> str:
    if not prompt:
        return prompt

    has_host = HOST_CONTEXT_MARKER in prompt
    recipe_block = build_plugin_recipe_block(prompt)

    if has_host and not recipe_block:
        return prompt
    if has_host and recipe_block:
        return recipe_block + prompt

    inputs = parse_inputs(inputs_raw)
    platform = str(inputs.get("platform") or "").strip()
    runtimes_str = str(runtimes or "").strip()

    parts: list[str] = []
    if recipe_block:
        parts.append(recipe_block)
    if platform or runtimes_str:
        repo_name = str(inputs.get("repository-name") or "").strip()
        parts.append(build_host_context_block(platform, repo_name, runtimes_str))
    parts.append(prompt)
    return "".join(parts)

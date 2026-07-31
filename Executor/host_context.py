"""Prepend structural host context to Claude Code prompts.

When the control plane declares ``platform`` in ``XIANIX_INPUTS``, free-form
prompts (e.g. PR comment instructions) often omit the hosting service. Env vars
alone are easy for Claude Code to miss; a short prompt preamble makes GitHub vs
Azure DevOps tool choice explicit. Detection from ``git remote`` remains
authoritative inside plugins — this block is a hint, not a substitute.

The same preamble also lists the slash commands of the plugins made available to
the run (``slash-command`` on each ``CLAUDE_CODE_PLUGINS`` descriptor), so a
free-form prompt that never names the command doesn't leave Claude Code guessing
which plugin command to invoke.
"""
from __future__ import annotations

import json

HOST_CONTEXT_MARKER = "[Xianix host context]"

# Per-platform tool hint, attached inline to the `platform:` line so it's unambiguous which
# host API/CLI to reach for. Only the matching platform's hint is shown (rather than a generic
# GitHub-vs-Azure-DevOps rundown), keeping the preamble short and free of irrelevant options.
_PLATFORM_TOOL_HINTS = {
    "github": "use the `gh` CLI",
    "azuredevops": "use the Azure DevOps REST API (see providers/azure-devops.md), not `gh`",
}
_PLATFORM_TOOL_HINT_FALLBACK = "use this platform's native API/CLI"

_PLUGIN_COMMANDS_INTRO = (
    "Available plugin commands — invoke the exact slash command shown "
    "(don't invent alternate command names):"
)


def _platform_line(platform: str) -> str:
    """`platform:` line with an inline, platform-specific tool hint.

    e.g. ``platform: github — for PR/issue/comment API calls use the `gh` CLI (confirm with
    `git remote get-url origin`).`` Unknown platforms get a generic fallback rather than
    naming the wrong tool.
    """
    tool = _PLATFORM_TOOL_HINTS.get(platform.strip().lower(), _PLATFORM_TOOL_HINT_FALLBACK)
    return (
        f"platform: {platform} — for PR/issue/comment API calls {tool} "
        "(confirm with `git remote get-url origin`)"
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


def plugin_command_lines(plugins: list[dict] | None) -> list[str]:
    """Render one bullet per plugin that declares a ``slash-command``.

    Deduplicates by command (two executions sharing ``/pr-review`` list it once) and
    skips descriptors without a command. Returns ``[]`` when nothing is listable.
    """
    lines: list[str] = []
    seen: set[str] = set()
    for plugin in plugins or []:
        if not isinstance(plugin, dict):
            continue
        command = str(plugin.get("slash-command") or "").strip()
        if not command or command in seen:
            continue
        seen.add(command)
        name = str(plugin.get("plugin-name") or "").strip()
        lines.append(f"- {command} — {name}" if name else f"- {command}")
    return lines


def build_host_context_block(
    platform: str,
    repository_name: str = "",
    command_lines: list[str] | None = None,
    runtimes: str = "",
) -> str:
    """Build the preamble block from a platform hint, plugin command list, and/or runtimes.

    At least one of ``platform``, ``command_lines``, or ``runtimes`` should be non-empty;
    callers use :func:`prepend_host_context` which only builds the block when there's
    something to say.
    """
    lines = [HOST_CONTEXT_MARKER]

    if platform:
        lines.append(_platform_line(platform))
        if repository_name:
            lines.append(f"repository-name: {repository_name}")

    if runtimes:
        lines.append(
            f"provisioned runtimes (already installed and on PATH): {runtimes}"
        )

    if command_lines:
        if platform or runtimes:
            lines.append("")
        lines.append(_PLUGIN_COMMANDS_INTRO)
        lines.extend(command_lines)

    lines.append("")
    lines.append("---")
    # Trailing blank line so the original prompt starts on its own paragraph.
    return "\n".join(lines) + "\n\n"


def prepend_host_context(
    prompt: str,
    inputs_raw: str | None,
    plugins: list[dict] | None = None,
    runtimes: str | None = None,
) -> str:
    """Prepend host context when there's a platform, plugin commands, or runtimes to surface.

    ``runtimes`` is the human-readable summary exported by ``provision_runtimes.sh``
    (``XIANIX_PROVISIONED_RUNTIMES``, e.g. ``dotnet 9.0; node 22.11.0``) — surfacing it
    tells the agent those tools are already on PATH so it doesn't waste turns probing
    or trying to install them.

    Returns ``prompt`` unchanged when there is nothing to surface. Idempotent: if
    ``prompt`` already starts with the host-context marker, return it as-is. Does not
    invent a platform when the key is absent or empty.
    """
    if not prompt:
        return prompt

    stripped = prompt.lstrip()
    if stripped.startswith(HOST_CONTEXT_MARKER):
        return prompt

    inputs = parse_inputs(inputs_raw)
    platform = str(inputs.get("platform") or "").strip()
    command_lines = plugin_command_lines(plugins)
    runtimes_str = str(runtimes or "").strip()

    if not platform and not command_lines and not runtimes_str:
        return prompt

    repo_name = str(inputs.get("repository-name") or "").strip()
    return build_host_context_block(platform, repo_name, command_lines, runtimes_str) + prompt

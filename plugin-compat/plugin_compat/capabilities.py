"""Capability and construct detection for PluginDefinition."""

from __future__ import annotations

import re
from typing import TYPE_CHECKING

from .constructs import Capability, ClaudeConstruct

if TYPE_CHECKING:
    from .model import PluginDefinition

_TASK_RE = re.compile(r"\bTask\b")
_AGENT_TOOL_RE = re.compile(r'(?:["\']tool["\']\s*:\s*["\']Agent["\']|\bAgent\b\s*/\s*`?Task)')
_SUBAGENT_RE = re.compile(r"subagent_type")
_MODEL_TIER_RE = re.compile(
    r"""(?:["']model["']\s*:\s*["'](?:haiku|sonnet|opus|fable)["']"""
    r"""|model:\s*(?:haiku|sonnet|opus|fable)\b)"""
)
_CLAUDE_ROOT_RE = re.compile(r"CLAUDE_PLUGIN_ROOT|\$\{CLAUDE_PLUGIN_ROOT\}")
_DISABLE_MODEL_RE = re.compile(r"disable-model-invocation")


def detect_capabilities(plugin: PluginDefinition) -> tuple[Capability, ...]:
    """Infer present capabilities from parsed plugin structure."""
    caps: list[Capability] = []
    if plugin.commands:
        caps.append(Capability.COMMANDS)
    if plugin.agents:
        caps.append(Capability.AGENTS)
    if plugin.skills:
        caps.append(Capability.SKILLS)
    if plugin.scripts:
        caps.append(Capability.SCRIPTS)
    if plugin.hooks:
        caps.append(Capability.HOOKS)

    template_kinds = {t.kind for t in plugin.templates}
    if "styles" in template_kinds:
        caps.append(Capability.TEMPLATES)
    if "providers" in template_kinds:
        caps.append(Capability.PROVIDERS)
    if "schemas" in template_kinds:
        caps.append(Capability.SCHEMAS)

    if plugin.metadata.get("has_lsp"):
        caps.append(Capability.LSP)
    if plugin.metadata.get("has_settings"):
        caps.append(Capability.SETTINGS)

    return tuple(caps)


def detect_constructs_in_text(text: str) -> tuple[ClaudeConstruct, ...]:
    """Detect Claude-specific constructs in markdown/script text."""
    found: list[ClaudeConstruct] = []
    if _TASK_RE.search(text):
        found.append(ClaudeConstruct.TASK)
    if _SUBAGENT_RE.search(text):
        found.append(ClaudeConstruct.SUBAGENT_TYPE)
    agent_tool_match = _AGENT_TOOL_RE.search(text)
    if agent_tool_match or (
        "tools:" in text[:500] and re.search(r"\bAgent\b", text[:800])
    ):
        # Frontmatter tools: Agent — keep lightweight
        if agent_tool_match or re.search(r"(?m)^tools:.*\bAgent\b", text):
            found.append(ClaudeConstruct.AGENT_TOOL)
    if _MODEL_TIER_RE.search(text):
        found.append(ClaudeConstruct.MODEL_TIER)
    if _CLAUDE_ROOT_RE.search(text):
        found.append(ClaudeConstruct.CLAUDE_PLUGIN_ROOT)
    if _DISABLE_MODEL_RE.search(text):
        found.append(ClaudeConstruct.DISABLE_MODEL_INVOCATION)
    # Deduplicate preserving order
    seen: set[ClaudeConstruct] = set()
    ordered: list[ClaudeConstruct] = []
    for item in found:
        if item not in seen:
            seen.add(item)
            ordered.append(item)
    return tuple(ordered)

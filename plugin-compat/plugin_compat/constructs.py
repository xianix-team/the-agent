"""Known plugin capabilities and Claude-specific constructs.

Capabilities are high-level features a plugin may expose.
Constructs are Claude-runtime idioms detected inside content that may need translation.
"""

from __future__ import annotations

from enum import Enum


class Capability(str, Enum):
    """Top-level plugin capability categories."""

    COMMANDS = "commands"
    AGENTS = "agents"
    SKILLS = "skills"
    SCRIPTS = "scripts"
    HOOKS = "hooks"
    TEMPLATES = "templates"
    PROVIDERS = "providers"
    SCHEMAS = "schemas"
    LSP = "lsp"
    SETTINGS = "settings"


class ClaudeConstruct(str, Enum):
    """Claude Code constructs detected in plugin content."""

    TASK = "Task"
    AGENT_TOOL = "Agent"
    SUBAGENT_TYPE = "subagent_type"
    MODEL_TIER = "model_tier"
    CLAUDE_PLUGIN_ROOT = "CLAUDE_PLUGIN_ROOT"
    DISABLE_MODEL_INVOCATION = "disable-model-invocation"
    PRE_TOOL_USE = "PreToolUse"
    POST_TOOL_USE = "PostToolUse"
    SESSION_START = "SessionStart"


class SupportLevel(str, Enum):
    """How a harness handles a capability."""

    NATIVE = "native"
    SUPPORTED = "supported"  # via adapter translation
    REUSABLE = "reusable"  # copy/run as-is
    PARTIAL = "partial"
    UNSUPPORTED = "unsupported"
    DIFFERENT = "different"  # exists but semantics differ (e.g. model tiers)
    NOT_APPLICABLE = "n/a"

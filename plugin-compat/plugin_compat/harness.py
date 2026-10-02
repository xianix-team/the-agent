"""Harness capability matrices.

Represented in code (not scattered executor conditionals).
"""

from __future__ import annotations

from dataclasses import dataclass

from .constructs import Capability, SupportLevel


@dataclass(frozen=True)
class HarnessCapabilities:
    name: str
    levels: dict[Capability, SupportLevel]
    notes: dict[Capability, str] | None = None

    def level_for(self, capability: Capability) -> SupportLevel:
        return self.levels.get(capability, SupportLevel.UNSUPPORTED)

    def note_for(self, capability: Capability) -> str:
        if not self.notes:
            return ""
        return self.notes.get(capability, "")


CLAUDE_CODE = HarnessCapabilities(
    name="claude",
    levels={
        Capability.COMMANDS: SupportLevel.NATIVE,
        Capability.AGENTS: SupportLevel.NATIVE,
        Capability.SKILLS: SupportLevel.NATIVE,
        Capability.SCRIPTS: SupportLevel.NATIVE,
        Capability.HOOKS: SupportLevel.NATIVE,
        Capability.TEMPLATES: SupportLevel.NATIVE,
        Capability.PROVIDERS: SupportLevel.NATIVE,
        Capability.SCHEMAS: SupportLevel.NATIVE,
        Capability.LSP: SupportLevel.NATIVE,
        Capability.SETTINGS: SupportLevel.NATIVE,
    },
)

OPENCODE = HarnessCapabilities(
    name="opencode",
    levels={
        Capability.COMMANDS: SupportLevel.SUPPORTED,
        Capability.AGENTS: SupportLevel.SUPPORTED,
        Capability.SKILLS: SupportLevel.SUPPORTED,
        Capability.SCRIPTS: SupportLevel.REUSABLE,
        Capability.HOOKS: SupportLevel.PARTIAL,
        Capability.TEMPLATES: SupportLevel.REUSABLE,
        Capability.PROVIDERS: SupportLevel.REUSABLE,
        Capability.SCHEMAS: SupportLevel.REUSABLE,
        Capability.LSP: SupportLevel.UNSUPPORTED,
        Capability.SETTINGS: SupportLevel.DIFFERENT,
    },
    notes={
        Capability.COMMANDS: "translate to OpenCode commands / recipe injection",
        Capability.AGENTS: "translate to OpenCode agents (mode: subagent)",
        Capability.SKILLS: "translate to .opencode/skills",
        Capability.SCRIPTS: "reuse under plugin root; set CLAUDE_PLUGIN_ROOT",
        Capability.HOOKS: "no PreToolUse intercept; recipe gates only",
        Capability.TEMPLATES: "copy styles/templates",
        Capability.PROVIDERS: "copy provider markdown",
        Capability.SCHEMAS: "copy schema files",
        Capability.LSP: "Claude LSP registration not mapped",
        Capability.SETTINGS: "Claude settings.json not OpenCode-native",
    },
)

HARNESS_CAPABILITIES: dict[str, HarnessCapabilities] = {
    CLAUDE_CODE.name: CLAUDE_CODE,
    OPENCODE.name: OPENCODE,
}


def get_harness_capabilities(harness: str) -> HarnessCapabilities:
    key = harness.strip().lower()
    if key not in HARNESS_CAPABILITIES:
        known = ", ".join(sorted(HARNESS_CAPABILITIES))
        raise KeyError(f"Unknown harness '{harness}'. Known: {known}")
    return HARNESS_CAPABILITIES[key]

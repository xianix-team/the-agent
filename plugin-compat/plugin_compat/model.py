"""Harness-neutral plugin model."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any

from .constructs import Capability, ClaudeConstruct, SupportLevel


@dataclass(frozen=True)
class Frontmatter:
    """Parsed YAML-ish frontmatter from a markdown plugin asset."""

    raw: dict[str, Any] = field(default_factory=dict)
    name: str | None = None
    description: str | None = None
    tools: tuple[str, ...] = ()
    model: str | None = None
    disable_model_invocation: bool = False
    extra: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class CommandDefinition:
    id: str
    path: str
    body: str
    frontmatter: Frontmatter
    constructs: tuple[ClaudeConstruct, ...] = ()
    registered: bool = False


@dataclass(frozen=True)
class AgentDefinition:
    id: str
    path: str
    body: str
    frontmatter: Frontmatter
    constructs: tuple[ClaudeConstruct, ...] = ()
    registered: bool = False


@dataclass(frozen=True)
class SkillDefinition:
    id: str
    path: str
    body: str
    frontmatter: Frontmatter
    constructs: tuple[ClaudeConstruct, ...] = ()
    registered: bool = False


@dataclass(frozen=True)
class ScriptDefinition:
    id: str
    path: str
    relative_path: str


@dataclass(frozen=True)
class HookDefinition:
    event: str
    matcher: str | None
    command: str | None
    script_path: str | None
    constructs: tuple[ClaudeConstruct, ...] = ()
    registered: bool = False


@dataclass(frozen=True)
class TemplateDefinition:
    id: str
    path: str
    kind: str  # styles | providers | schemas | other


@dataclass(frozen=True)
class PluginDefinition:
    """Harness-neutral representation of a Xianix/Claude plugin."""

    id: str
    name: str
    version: str
    root: str
    description: str = ""
    source_hash: str = ""
    commands: tuple[CommandDefinition, ...] = ()
    agents: tuple[AgentDefinition, ...] = ()
    skills: tuple[SkillDefinition, ...] = ()
    scripts: tuple[ScriptDefinition, ...] = ()
    hooks: tuple[HookDefinition, ...] = ()
    templates: tuple[TemplateDefinition, ...] = ()
    capabilities: tuple[Capability, ...] = ()
    constructs: tuple[ClaudeConstruct, ...] = ()
    metadata: dict[str, Any] = field(default_factory=dict)


class CompatibilityStatus(str, Enum):
    SUPPORTED = "SUPPORTED"
    SUPPORTED_WITH_LIMITATIONS = "SUPPORTED_WITH_LIMITATIONS"
    UNSUPPORTED = "UNSUPPORTED"


@dataclass(frozen=True)
class CapabilityAssessment:
    capability: Capability
    present: bool
    required: bool
    support: SupportLevel
    detail: str = ""


@dataclass(frozen=True)
class CompatibilityResult:
    plugin_id: str
    harness: str
    status: CompatibilityStatus
    assessments: tuple[CapabilityAssessment, ...]
    unsupported_required: tuple[Capability, ...] = ()
    partial_or_optional_gaps: tuple[Capability, ...] = ()
    detected_constructs: tuple[ClaudeConstruct, ...] = ()
    notes: tuple[str, ...] = ()

    def summary_lines(self) -> list[str]:
        lines = [
            f"Plugin: {self.plugin_id}",
            f"Harness: {self.harness}",
            "",
            "Capabilities:",
        ]
        for item in self.assessments:
            if not item.present:
                continue
            req = "required" if item.required else "optional"
            detail = f" ({item.detail})" if item.detail else ""
            lines.append(
                f"  {item.capability.value:12} {item.support.value:12} [{req}]{detail}"
            )
        lines.append("")
        lines.append(f"Compatibility: {self.status.value}")
        return lines

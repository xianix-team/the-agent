"""Compatibility validation: PluginDefinition × HarnessCapabilities."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from .constructs import Capability, SupportLevel
from .harness import get_harness_capabilities
from .model import (
    CapabilityAssessment,
    CompatibilityResult,
    CompatibilityStatus,
    PluginDefinition,
)

# Capabilities treated as required when present on the plugin (Phase 1 default).
_DEFAULT_REQUIRED: frozenset[Capability] = frozenset(
    {
        Capability.COMMANDS,
        Capability.AGENTS,
        Capability.SKILLS,
        Capability.SCRIPTS,
        Capability.TEMPLATES,
        Capability.PROVIDERS,
        Capability.SCHEMAS,
    }
)

# Present but optional unless external metadata marks them required.
_DEFAULT_OPTIONAL: frozenset[Capability] = frozenset(
    {
        Capability.HOOKS,
        Capability.LSP,
        Capability.SETTINGS,
    }
)

_FULLY_OK = frozenset(
    {
        SupportLevel.NATIVE,
        SupportLevel.SUPPORTED,
        SupportLevel.REUSABLE,
    }
)

_LIMITED = frozenset(
    {
        SupportLevel.PARTIAL,
        SupportLevel.DIFFERENT,
    }
)


def validate_compatibility(
    plugin: PluginDefinition,
    harness: str,
    *,
    metadata_path: str | Path | None = None,
) -> CompatibilityResult:
    """Assess whether a plugin can run on the given harness.

    Optional external metadata (YAML) may override required/optional sets.
    It is never required for normal plugins.
    """
    harness_caps = get_harness_capabilities(harness)
    overrides = _load_metadata(metadata_path) if metadata_path else None

    required, optional = _resolve_requirement_sets(plugin, overrides)

    assessments: list[CapabilityAssessment] = []
    unsupported_required: list[Capability] = []
    partial_gaps: list[Capability] = []

    for capability in Capability:
        present = capability in plugin.capabilities
        if not present:
            continue

        is_required = capability in required
        if capability in optional and capability not in required:
            is_required = False

        support = harness_caps.level_for(capability)
        detail = harness_caps.note_for(capability)
        assessments.append(
            CapabilityAssessment(
                capability=capability,
                present=True,
                required=is_required,
                support=support,
                detail=detail,
            )
        )

        if support == SupportLevel.UNSUPPORTED:
            if is_required:
                unsupported_required.append(capability)
            else:
                partial_gaps.append(capability)
        elif support in _LIMITED:
            partial_gaps.append(capability)

    if unsupported_required:
        status = CompatibilityStatus.UNSUPPORTED
    elif partial_gaps:
        status = CompatibilityStatus.SUPPORTED_WITH_LIMITATIONS
    else:
        status = CompatibilityStatus.SUPPORTED

    notes: list[str] = []
    gaps = plugin.metadata.get("registration_gaps") or {}
    for key, values in gaps.items():
        if values:
            notes.append(f"{key}: {', '.join(values)}")

    return CompatibilityResult(
        plugin_id=plugin.id,
        harness=harness_caps.name,
        status=status,
        assessments=tuple(assessments),
        unsupported_required=tuple(unsupported_required),
        partial_or_optional_gaps=tuple(partial_gaps),
        detected_constructs=plugin.constructs,
        notes=tuple(notes),
    )


def _resolve_requirement_sets(
    plugin: PluginDefinition,
    overrides: dict[str, Any] | None,
) -> tuple[set[Capability], set[Capability]]:
    present = set(plugin.capabilities)
    required = present & _DEFAULT_REQUIRED
    optional = present & _DEFAULT_OPTIONAL

    if not overrides:
        return required, optional

    req_names = overrides.get("required") or []
    opt_names = overrides.get("optional") or []
    if req_names:
        required = {_cap(name) for name in req_names} & present
    if opt_names:
        optional = {_cap(name) for name in opt_names} & present
        # Anything listed optional is not required
        required -= optional

    return required, optional


def _cap(name: str) -> Capability:
    return Capability(str(name).strip().lower())


def _load_metadata(path: str | Path) -> dict[str, Any]:
    """Load optional external compatibility metadata (JSON or simple YAML)."""
    meta_path = Path(path)
    if not meta_path.is_file():
        raise FileNotFoundError(f"Compatibility metadata not found: {meta_path}")
    text = meta_path.read_text(encoding="utf-8")
    if meta_path.suffix.lower() == ".json":
        data = json.loads(text)
    else:
        data = _parse_simple_compat_yaml(text)
    if data is None:
        return {}
    if not isinstance(data, dict):
        raise ValueError(f"Compatibility metadata must be a mapping: {meta_path}")
    return data


def _parse_simple_compat_yaml(text: str) -> dict[str, Any]:
    """Parse the small optional metadata shape without PyYAML.

    Supported example::

        plugin: pr-reviewer
        required:
          - commands
          - agents
        optional:
          - hooks
    """
    result: dict[str, Any] = {}
    current_list_key: str | None = None
    for raw_line in text.splitlines():
        if not raw_line.strip() or raw_line.strip().startswith("#"):
            continue
        list_item = re.match(r"^\s+-\s+(.+)$", raw_line)
        if list_item and current_list_key:
            result.setdefault(current_list_key, []).append(list_item.group(1).strip())
            continue
        kv = re.match(r"^([A-Za-z0-9_-]+):\s*(.*)$", raw_line)
        if not kv:
            continue
        key, value = kv.group(1), kv.group(2).strip()
        if value == "":
            current_list_key = key
            result[key] = []
        else:
            current_list_key = None
            result[key] = value.strip('"').strip("'")
    return result

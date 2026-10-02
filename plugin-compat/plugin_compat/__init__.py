"""Harness-neutral plugin compatibility layer for Xianix."""

from .adapters.opencode import GeneratedBundle, OpenCodePluginAdapter
from .capabilities import detect_capabilities
from .harness import HARNESS_CAPABILITIES, get_harness_capabilities
from .model import CompatibilityResult, CompatibilityStatus, PluginDefinition
from .parser import parse_plugin
from .validator import validate_compatibility

__all__ = [
    "CompatibilityResult",
    "CompatibilityStatus",
    "GeneratedBundle",
    "HARNESS_CAPABILITIES",
    "OpenCodePluginAdapter",
    "PluginDefinition",
    "detect_capabilities",
    "get_harness_capabilities",
    "parse_plugin",
    "validate_compatibility",
]

__version__ = "0.2.0"

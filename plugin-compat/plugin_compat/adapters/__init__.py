"""Harness adapters (one per harness, not per plugin)."""

from .opencode import GeneratedBundle, OpenCodePluginAdapter

__all__ = ["GeneratedBundle", "OpenCodePluginAdapter"]

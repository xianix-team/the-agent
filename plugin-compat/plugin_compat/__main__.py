"""CLI: parse a Claude/Xianix plugin and generate an OpenCode bundle."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .adapters.opencode import CompatibilityError, OpenCodePluginAdapter
from .parser import PluginParseError, parse_plugin
from .validator import validate_compatibility


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Xianix plugin-compat: adapt a plugin for OpenCode",
    )
    parser.add_argument(
        "--plugin-root",
        required=True,
        help="Path to canonical Claude/Xianix plugin directory",
    )
    parser.add_argument(
        "--output",
        required=True,
        help="Output directory for the generated OpenCode bundle",
    )
    parser.add_argument(
        "--harness",
        default="opencode",
        help="Target harness (default: opencode)",
    )
    parser.add_argument(
        "--metadata",
        default=None,
        help="Optional external compatibility metadata (YAML/JSON)",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Regenerate even if cache key matches",
    )
    parser.add_argument(
        "--allow-unsupported",
        action="store_true",
        help="Do not fail when required capabilities are unsupported",
    )
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Parse + validate only; do not generate a bundle",
    )
    args = parser.parse_args(argv)

    try:
        plugin = parse_plugin(args.plugin_root)
    except PluginParseError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    if args.validate_only or args.harness != "opencode":
        result = validate_compatibility(
            plugin, args.harness, metadata_path=args.metadata
        )
        print("\n".join(result.summary_lines()))
        return 0 if result.status.value != "UNSUPPORTED" else 3

    adapter = OpenCodePluginAdapter()
    try:
        bundle = adapter.adapt(
            plugin,
            args.output,
            metadata_path=args.metadata,
            fail_on_unsupported=not args.allow_unsupported,
            force=args.force,
        )
    except CompatibilityError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 3

    print("\n".join(bundle.compatibility.summary_lines()))
    print()
    print(json.dumps(bundle.version_info(), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

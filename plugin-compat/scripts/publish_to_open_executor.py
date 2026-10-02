"""Publish canonical plugins + plugin-compat into xianix-open-executor for image builds.

Does not modify official plugins. Copies source trees used at OpenCode runtime
for generic adaptation (parse → PluginDefinition → OpenCodePluginAdapter).

Usage (from the-agent root):

  python plugin-compat/scripts/publish_to_open_executor.py
  python plugin-compat/scripts/publish_to_open_executor.py --plugin pr-reviewer --plugin doc-writer
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
COMPAT_ROOT = SCRIPT_DIR.parent
THE_AGENT = COMPAT_ROOT.parent
OPEN_EXECUTOR = THE_AGENT / "xianix-open-executor"
DEFAULT_PLUGINS_ROOT = THE_AGENT.parent / "plugins-official" / "plugins"


def _git_commit(path: Path) -> str:
    for start in (path, path.parent, path.parent.parent):
        try:
            out = subprocess.check_output(
                ["git", "-C", str(start), "rev-parse", "HEAD"],
                stderr=subprocess.DEVNULL,
                text=True,
            ).strip()
            if out:
                return out
        except (subprocess.CalledProcessError, FileNotFoundError, OSError):
            continue
    return "unknown"


def _copy_tree(src: Path, dest: Path) -> None:
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(
        src,
        dest,
        ignore=shutil.ignore_patterns(
            ".git",
            "__pycache__",
            "*.pyc",
            ".pytest_cache",
            "node_modules",
        ),
    )


def publish_plugin_compat(dest_root: Path) -> None:
    package_src = COMPAT_ROOT / "plugin_compat"
    package_dest = dest_root / "plugin_compat"
    if package_dest.exists():
        shutil.rmtree(package_dest)
    shutil.copytree(
        package_src,
        package_dest,
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
    )
    print(f"Published plugin_compat → {package_dest}")


def publish_plugin(plugin_name: str, plugins_root: Path, dest_root: Path) -> Path:
    src = plugins_root / plugin_name
    if not src.is_dir():
        raise FileNotFoundError(f"Plugin not found: {src}")
    dest = dest_root / "plugins" / plugin_name
    _copy_tree(src, dest)

    manifest = src / ".claude-plugin" / "plugin.json"
    version = "unknown"
    if manifest.is_file():
        data = json.loads(manifest.read_text(encoding="utf-8-sig"))
        version = str(data.get("version") or "unknown")

    meta = {
        "source_plugin": plugin_name,
        "source_plugin_version": version,
        "source_plugin_commit": _git_commit(src),
        "published_at": datetime.now(timezone.utc).isoformat(),
        "note": "Canonical plugin snapshot for OpenCode adaptation — not a second SoT",
    }
    (dest / "SOURCE.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"Published plugin {plugin_name}@{version} → {dest}")
    return dest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--plugin",
        action="append",
        dest="plugins",
        default=None,
        help="Plugin directory name under plugins-official/plugins (repeatable)",
    )
    parser.add_argument(
        "--plugins-root",
        default=str(DEFAULT_PLUGINS_ROOT),
        help="Path to plugins-official/plugins",
    )
    parser.add_argument(
        "--executor-root",
        default=str(OPEN_EXECUTOR),
        help="Path to xianix-open-executor",
    )
    parser.add_argument(
        "--generate",
        action="store_true",
        help="Also generate an OpenCode bundle under vendor/generated/<plugin>",
    )
    args = parser.parse_args(argv)

    plugins = args.plugins or ["pr-reviewer", "doc-writer"]
    plugins_root = Path(args.plugins_root)
    executor = Path(args.executor_root)
    vendor = executor / "vendor"
    vendor.mkdir(parents=True, exist_ok=True)

    if not plugins_root.is_dir():
        print(f"ERROR: plugins root not found: {plugins_root}", file=sys.stderr)
        return 1

    publish_plugin_compat(vendor)

    # Ensure plugin-compat is importable when generating
    sys.path.insert(0, str(COMPAT_ROOT))

    for name in plugins:
        plugin_dest = publish_plugin(name, plugins_root, vendor)
        if args.generate:
            from plugin_compat.adapters.opencode import OpenCodePluginAdapter
            from plugin_compat.parser import parse_plugin

            plugin = parse_plugin(plugin_dest)
            out = vendor / "generated" / name
            bundle = OpenCodePluginAdapter().adapt(plugin, out, force=True)
            print(
                f"Generated OpenCode bundle for {name}: "
                f"{bundle.compatibility.status.value} → {out}"
            )

    # vendor/ is gitignored; runtime generation inside the executor is preferred.
    print("Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

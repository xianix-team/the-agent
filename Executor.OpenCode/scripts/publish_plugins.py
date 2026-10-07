"""Publish official plugin trees into Executor.OpenCode/vendor for image builds.

Copies Claude plugin trees (commands/agents/skills + shared scripts/providers)
into vendor/plugins/. OpenCode converts markdown at runtime.

Usage (from the-agent root):

  python Executor.OpenCode/scripts/publish_plugins.py
  python Executor.OpenCode/scripts/publish_plugins.py --plugin pr-reviewer --plugin doc-writer
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
OPEN_EXECUTOR = SCRIPT_DIR.parent
THE_AGENT = OPEN_EXECUTOR.parent
DEFAULT_PLUGINS_ROOT = THE_AGENT.parent / "plugins-official" / "plugins"


def _git_commit(path: Path) -> str:
    for start in (path, path.parent, path.parent.parent):
        try:
            out = subprocess.check_output(
                ["git", "-C", str(start.resolve()), "rev-parse", "HEAD"],
                stderr=subprocess.DEVNULL,
                text=True,
            ).strip()
            if out:
                return out
        except (subprocess.CalledProcessError, FileNotFoundError, OSError):
            continue
    return "unknown"


def _validate_plugin_name(plugin_name: str) -> None:
    if (
        not plugin_name
        or plugin_name in {".", ".."}
        or plugin_name.startswith(".")
        or "/" in plugin_name
        or "\\" in plugin_name
    ):
        raise ValueError(f"Invalid plugin name: {plugin_name}")


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


def publish_plugin(plugin_name: str, plugins_root: Path, dest_root: Path) -> Path:
    _validate_plugin_name(plugin_name)
    src = (plugins_root / plugin_name).resolve()
    plugins_root_resolved = plugins_root.resolve()
    try:
        src.relative_to(plugins_root_resolved)
    except ValueError as exc:
        raise ValueError(f"Invalid plugin name: {plugin_name}") from exc
    if not src.is_dir():
        raise FileNotFoundError(f"Plugin not found: {src}")
    if not (src / ".claude-plugin" / "plugin.json").is_file() and not (
        src / "commands"
    ).is_dir():
        raise FileNotFoundError(
            f"Plugin '{plugin_name}' has no .claude-plugin/plugin.json or commands/"
        )

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
        "note": "Claude plugin snapshot; OpenCode converts commands/agents/skills .md at runtime",
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
        help="Path to Executor.OpenCode",
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

    for name in plugins:
        try:
            publish_plugin(name, plugins_root, vendor)
        except (FileNotFoundError, ValueError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 1

    print("Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

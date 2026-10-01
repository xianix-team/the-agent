"""Generic OpenCode plugin adaptation runtime (harness-level, not plugin-specific)."""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path
from typing import Any

# Prefer vendored copy inside the executor image; fall back to sibling checkout.
_VENDOR_COMPAT = Path("/workspace/vendor/plugin_compat")
_LOCAL_COMPAT = Path(__file__).resolve().parent / "vendor" / "plugin_compat"
_DEV_COMPAT = Path(__file__).resolve().parents[1] / "plugin-compat"

for candidate in (_VENDOR_COMPAT.parent, _LOCAL_COMPAT.parent, _DEV_COMPAT):
    # candidate is parent of plugin_compat package dir when vendored as vendor/plugin_compat
    if (candidate / "plugin_compat").is_dir():
        sys.path.insert(0, str(candidate))
        break
    if candidate.name == "plugin-compat" and (candidate / "plugin_compat").is_dir():
        sys.path.insert(0, str(candidate))
        break

from plugin_compat.adapters.opencode import (  # noqa: E402
    CompatibilityError,
    GeneratedBundle,
    OpenCodePluginAdapter,
)
from plugin_compat.model import CompatibilityStatus  # noqa: E402
from plugin_compat.parser import PluginParseError, parse_plugin  # noqa: E402

DEFAULT_PLUGINS_DIR = "/workspace/vendor/plugins"
DEFAULT_GENERATED_DIR = "/workspace/generated-plugins"
ADAPTER_VERSION = "0.2.0"


def discover_plugin_roots(plugins_dir: str | None = None) -> list[Path]:
    """List plugin directories that contain .claude-plugin/plugin.json."""
    root = Path(
        plugins_dir
        or os.environ.get("XIANIX_PLUGINS_DIR")
        or DEFAULT_PLUGINS_DIR
    )
    if not root.is_dir():
        return []
    found: list[Path] = []
    for child in sorted(root.iterdir()):
        if child.is_dir() and (child / ".claude-plugin" / "plugin.json").is_file():
            found.append(child)
    return found


def resolve_requested_plugins() -> list[str]:
    """Plugin names from CLAUDE_CODE_PLUGINS JSON, if present."""
    raw = os.environ.get("CLAUDE_CODE_PLUGINS") or os.environ.get("CLAUDE-CODE-PLUGINS")
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return []
    names: list[str] = []
    if isinstance(data, list):
        for item in data:
            if not isinstance(item, dict):
                continue
            name = str(item.get("plugin-name") or item.get("name") or "")
            # plugin-name may be "pr-reviewer@marketplace"
            name = name.split("@", 1)[0].strip()
            if name:
                names.append(name)
    return names


def match_slash_command(prompt: str, command_ids: list[str]) -> str | None:
    """Return the first command id whose /name appears in the prompt."""
    if not prompt:
        return None
    for cmd in command_ids:
        pattern = re.compile(rf"(?i)/(?:{re.escape(cmd)})\b")
        if pattern.search(prompt):
            return cmd
    return None


def adapt_plugin(
    plugin_root: Path,
    output_dir: Path,
    *,
    force: bool = False,
) -> GeneratedBundle:
    plugin = parse_plugin(plugin_root)
    adapter = OpenCodePluginAdapter(adapter_version=ADAPTER_VERSION)
    return adapter.adapt(plugin, output_dir, force=force)


def prepare_plugins_for_prompt(
    prompt: str,
    *,
    work_dir: str | None = None,
    plugins_dir: str | None = None,
    generated_dir: str | None = None,
) -> dict[str, Any] | None:
    """Adapt available plugins and select a lead command when the prompt matches.

    Returns version/observability info for logging, or None when no plugins apply.
    Raises on UNSUPPORTED required capabilities or missing required assets when
    a slash command was matched.
    """
    # Empty CLAUDE-CODE-PLUGINS (no use-plugins on the rule) → skip adapter.
    # Plain OpenCode runs the execute-prompt without generating a plugin bundle.
    requested = set(resolve_requested_plugins())
    if not requested:
        return None

    roots = discover_plugin_roots(plugins_dir)
    if not roots:
        raise RuntimeError(
            "use-plugins requested "
            + ", ".join(sorted(requested))
            + f" but no plugins found under {plugins_dir or os.environ.get('XIANIX_PLUGINS_DIR') or DEFAULT_PLUGINS_DIR}"
        )

    matched_roots = [r for r in roots if r.name in requested]
    if not matched_roots:
        available = ", ".join(r.name for r in roots) or "(none)"
        raise RuntimeError(
            "Requested plugins not published in executor vendor: "
            + ", ".join(sorted(requested))
            + f". Available: {available}"
        )
    roots = matched_roots

    gen_root = Path(
        generated_dir
        or os.environ.get("XIANIX_GENERATED_PLUGINS_DIR")
        or DEFAULT_GENERATED_DIR
    )
    gen_root.mkdir(parents=True, exist_ok=True)

    adapted: list[GeneratedBundle] = []
    all_commands: list[str] = []
    command_to_bundle: dict[str, GeneratedBundle] = {}

    for root in roots:
        out = gen_root / root.name
        try:
            bundle = adapt_plugin(root, out)
        except (PluginParseError, CompatibilityError) as exc:
            raise RuntimeError(f"FATAL: plugin adaptation failed for {root.name}: {exc}") from exc
        if bundle.compatibility.status == CompatibilityStatus.UNSUPPORTED:
            raise RuntimeError(
                f"FATAL: plugin '{bundle.plugin_id}' is UNSUPPORTED on OpenCode. "
                f"See {out / 'COMPATIBILITY.txt'}"
            )
        adapted.append(bundle)
        for cmd in bundle.commands:
            all_commands.append(cmd)
            command_to_bundle[cmd] = bundle

    matched = match_slash_command(prompt, all_commands)
    primary = command_to_bundle.get(matched) if matched else (adapted[0] if adapted else None)

    # Install OpenCode project overlay into the workspace when possible.
    if primary and work_dir:
        _install_opencode_overlay(primary, Path(work_dir))

    # Export plugin root for scripts (CLAUDE_PLUGIN_ROOT compat).
    if primary:
        os.environ["CLAUDE_PLUGIN_ROOT"] = str(primary.output_dir)
        os.environ["XIANIX_PLUGIN_ROOT"] = str(primary.output_dir)
        os.environ["XIANIX_PLUGIN_ID"] = primary.plugin_id
        if primary.lead_command_path:
            # Prefer matched command file when available
            lead = primary.lead_command_path
            if matched:
                candidate = (
                    primary.output_dir / ".opencode" / "commands" / f"{matched}.md"
                )
                if candidate.is_file():
                    lead = str(candidate)
            os.environ["XIANIX_LEAD_COMMAND_PATH"] = lead

    info: dict[str, Any] = {
        "harness": "opencode",
        "adapter_version": ADAPTER_VERSION,
        "matched_command": matched,
        "plugins": [b.version_info() for b in adapted],
    }
    if primary:
        info.update(primary.version_info())
        info["compatibility_status"] = primary.compatibility.status.value
        info["plugin_root"] = str(primary.output_dir)
    return info


def _install_opencode_overlay(bundle: GeneratedBundle, work_dir: Path) -> None:
    """Copy generated .opencode/ into the workspace so `opencode run --dir` discovers it."""
    src = bundle.output_dir / ".opencode"
    if not src.is_dir():
        return
    dest = work_dir / ".opencode"
    if dest.exists():
        # Merge: replace commands/agents/skills from this bundle
        for sub in ("commands", "agents", "skills"):
            s = src / sub
            if not s.exists():
                continue
            d = dest / sub
            if d.exists():
                import shutil

                shutil.rmtree(d)
            import shutil

            shutil.copytree(s, d)
    else:
        import shutil

        shutil.copytree(src, dest)


def format_version_logs(info: dict[str, Any], model: str | None = None) -> list[str]:
    lines = [
        f"Harness: {info.get('harness', 'opencode')}",
        f"Model: {model or '(opencode default)'}",
        f"Adapter version: {info.get('adapter_version')}",
    ]
    if info.get("plugin"):
        lines.append(f"Plugin: {info.get('plugin')}")
        lines.append(f"Plugin version: {info.get('plugin_version')}")
        lines.append(f"Compatibility: {info.get('compatibility_status') or info.get('compatibility')}")
        lines.append(f"Cache hit: {info.get('cache_hit')}")
        lines.append(f"Plugin root: {info.get('plugin_root') or info.get('output_dir')}")
    if info.get("matched_command"):
        lines.append(f"Matched command: /{info['matched_command']}")
    return lines


# Backward-compatible aliases used by older execute_opencode imports.
def validate_adapter_or_raise(prompt: str, **_kwargs: Any) -> dict[str, Any] | None:
    """Adapt published plugins; fail closed when a matched slash command cannot run."""
    work_dir = os.environ.get("WORK_DIR") or os.environ.get("WORKDIR")
    info = prepare_plugins_for_prompt(prompt, work_dir=work_dir)
    if info is None:
        # Fall back: no vendored plugins — allow non-plugin prompts.
        return None
    # If prompt looks like a slash command but nothing matched and plugins exist,
    # still return info (model may follow host context without slash).
    return info

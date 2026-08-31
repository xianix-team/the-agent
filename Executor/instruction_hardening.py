"""Executor-owned instruction hardening for Claude Code runs.

Keeps a trust hierarchy (system prompt → plugin → untrusted webhook/repo data)
and provides deterministic PreToolUse checks plus canary exfiltration detection.
This module has no Claude SDK dependency so unit tests can run without Docker.
"""
from __future__ import annotations

import json
import os
import re
import uuid
from pathlib import Path

PROMPT_FILE_NAME = "system_prompt_hardening.md"
CANARY_PLACEHOLDER = "{{CANARY}}"
DEFAULT_DISALLOWED_TOOLS = ("WebSearch",)

_ENV_EXFIL_RE = re.compile(
    r"(printenv\b|"
    r"(?:^|[;&|\n]\s*)env(?:\s|$|[;&|])|"
    r"/proc/\S*environ|"
    r"os\.environ|"
    r"getenv\s*\(|"
    r"process\.env)",
    re.IGNORECASE,
)

_SECRET_PATH_RE = re.compile(
    r"(^|/)(proc/\S*environ|etc/environment|run/secrets)(/|$)|"
    r"\.aws/credentials|"
    r"\.docker/config\.json|"
    r"\.netrc$",
    re.IGNORECASE,
)

_PUSH_RE = re.compile(r"\bgit\s+(push|commit)\b", re.IGNORECASE)
_FIX_MODE_RE = re.compile(r"(--fix\b|apply fixes and push)", re.IGNORECASE)

_PROMPT_DIR = Path(__file__).resolve().parent


class HardeningError(RuntimeError):
    """Raised when hardening configuration is missing (fail closed)."""


class SecurityIncident(RuntimeError):
    """Raised when a canary leak or blocked exfiltration must halt the run."""

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


def new_canary() -> str:
    """Per-execution UUID. Never reuse a static public canary."""
    return str(uuid.uuid4())


def load_hardening_prompt_template() -> str:
    path = _PROMPT_DIR / PROMPT_FILE_NAME
    if not path.is_file():
        raise HardeningError(
            f"Hardened system prompt is missing at {path}. Refusing to start the LLM run."
        )
    text = path.read_text(encoding="utf-8").strip()
    if not text or CANARY_PLACEHOLDER not in text:
        raise HardeningError(
            f"Hardened system prompt at {path} is empty or missing {CANARY_PLACEHOLDER}."
        )
    return text


def render_system_prompt_append(canary: str) -> str:
    if not canary or not canary.strip():
        raise HardeningError("Canary must be a non-empty per-execution value.")
    return load_hardening_prompt_template().replace(CANARY_PLACEHOLDER, canary.strip())


def claude_system_prompt_option(canary: str) -> dict:
    """Preset + append so Claude Code built-in instructions are not replaced."""
    return {
        "type": "preset",
        "preset": "claude_code",
        "append": render_system_prompt_append(canary),
    }


def flatten_payload(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    try:
        return json.dumps(value, default=str)
    except TypeError:
        return str(value)


def contains_canary(text: str, canary: str) -> bool:
    if not canary or not text:
        return False
    return canary in text


def redact_canary(text: str, canary: str) -> str:
    if not canary or not text:
        return text
    return text.replace(canary, "[CANARY-REDACTED]")


def is_fix_mode(prompt: str) -> bool:
    return bool(_FIX_MODE_RE.search(prompt or ""))


def merge_disallowed_tools(configured: list[str]) -> list[str]:
    merged: list[str] = []
    seen: set[str] = set()
    for name in (*DEFAULT_DISALLOWED_TOOLS, *configured):
        clean = name.strip()
        if not clean:
            continue
        key = clean.lower()
        if key in seen:
            continue
        seen.add(key)
        merged.append(clean)
    return merged


def hardening_audit_mode() -> bool:
    return os.environ.get("XIANIX_HARDENING_AUDIT", "").strip().lower() in ("1", "true", "yes")


def evaluate_tool_use(
    tool_name: str,
    tool_input: object,
    *,
    canary: str,
    allow_mutates: bool,
) -> str | None:
    """Return a deny reason, or None to allow.

    Reasons never include the raw canary value.
    """
    blob = flatten_payload(tool_input)
    name = (tool_name or "").strip()

    if contains_canary(blob, canary) or contains_canary(name, canary):
        return "blocked: canary value present in tool arguments"

    if _is_read_like(name):
        path = _tool_path(tool_input)
        if path and _SECRET_PATH_RE.search(path.replace("\\", "/")):
            return f"blocked: refusing to read secret path {path}"

    if _is_bash_like(name):
        command = _bash_command(tool_input)
        if _ENV_EXFIL_RE.search(command):
            return "blocked: environment or credential access is not permitted"
        if _SECRET_PATH_RE.search(command.replace("\\", "/")):
            return "blocked: secret path access is not permitted"
        if not allow_mutates and _PUSH_RE.search(command):
            return "blocked: git commit/push is not permitted in report-only mode"

    return None


def _is_bash_like(name: str) -> bool:
    lowered = name.lower()
    return lowered in ("bash", "shell") or lowered.endswith("__bash")


def _is_read_like(name: str) -> bool:
    lowered = name.lower()
    return lowered in ("read", "read_file") or lowered.endswith("__read")


def _bash_command(tool_input: object) -> str:
    if isinstance(tool_input, dict):
        return str(tool_input.get("command") or "")
    return flatten_payload(tool_input)


def _tool_path(tool_input: object) -> str:
    if isinstance(tool_input, dict):
        return str(tool_input.get("file_path") or tool_input.get("path") or "")
    return flatten_payload(tool_input)


def wrap_interpolated_value(name: str, value: str | None) -> str:
    """Python equivalent of the C# interpolator — used only in executor tests."""
    escaped = (value or "").replace("</user_data>", "</ user_data>")
    safe_name = (name or "input").replace('"', "'")
    return f'<user_data name="{safe_name}">{escaped}</user_data>'

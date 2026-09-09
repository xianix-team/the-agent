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
    # Shell wrappers that dump env without a bare leading `env` token.
    r"(?:sh|bash|zsh|dash|fish|ksh)\s+-c\s+['\"]?(?:env|printenv|set|export|declare)\b|"
    r"\$\((?:env|printenv|set|export|declare)\)|"
    r"`(?:env|printenv|set|export|declare)`|"
    r"ruby\s+-e\s+['\"].*(?:ENV|env).*['\"]|"
    r"awk\s+.*ENVIRON|"
    r"systemctl\s+show-environment|"
    r"/proc/\S*environ|"
    r"os\.environ|"
    r"os\.getenv|"
    r"getenv\s*[\(\[]|"
    r"process\.env|"
    r"\$env:|"
    r"Get-ChildItem\s+Env:|"
    # Bare `set` / `set VAR` dumps the environment; allow `set -x` and `set-url`.
    r"(?:^|[;&|\n]\s*)set(?!\s*-)(?:\s|$|[;&|])|"
    r"declare\s+-p|"
    r"\bexport\b(?!\s+[A-Z_]+=)|"
    r"compgen\s+-e|"
    r"\$ENV[\[{]|"
    r"\$\{?[A-Z_][A-Z0-9_]*\}?)",
    re.IGNORECASE,
)

# Explicit secret file locations (checked against Read paths and Bash commands).
_SECRET_PATH_RE = re.compile(
    r"(^|/)(proc/\S*environ|etc/environment|run/secrets)(/|$)|"
    r"\.env(\.[A-Za-z0-9_-]+)?$|"
    r"\.aws/|"
    r"\.azure/|"
    r"\.config/gcloud/|"
    r"\.ssh/|"
    r"\.kube/config|"
    r"\.docker/config\.json|"
    r"\.netrc$|"
    r"credentials?\.json|"
    r"[/_-]service-?account[._-]|"
    r"\.(npmrc|pypirc|gemrc)$|"
    r"\.(bash|zsh|python)_history$|"
    r"/(passwd|shadow)$|"
    r"\.pem\b",
    re.IGNORECASE,
)

# Indirect Bash secret exfiltration (archives, recursive search, hex-encoded paths).
_INDIRECT_SECRET_ACCESS_RE = re.compile(
    r"\btar\b.*(?:~|/root|/home|\.ssh|\.aws|\.kube|\.docker|\.env)|"
    r"\b(?:grep|find)\b.*(?:/root|/home/[^/\s]+/\.|~|\.ssh|\.aws|\.pem|PRIVATE)|"
    r"printf.*(?:\\x2e\\x65\\x6e\\x76|\\x2f\\x65\\x74\\x63)",
    re.IGNORECASE,
)

# Absolute Read paths must stay under the executor worktree volume.
_WORKSPACE_PREFIX = "/workspace/"


_PUSH_RE = re.compile(r"\bgit\s+(push|commit)\b", re.IGNORECASE)
_FIX_MODE_RE = re.compile(r"(--fix\b|apply fixes and push)", re.IGNORECASE)
_USER_DATA_CLOSE_RE = re.compile(r"</user_data>", re.IGNORECASE)

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


def contains_canary_recursive(obj: object, canary: str) -> bool:
    """Walk nested tool inputs for a canary without JSON-serializing the payload."""
    if not canary:
        return False
    if isinstance(obj, str):
        return canary in obj
    if isinstance(obj, dict):
        return any(
            contains_canary_recursive(k, canary) or contains_canary_recursive(v, canary)
            for k, v in obj.items()
        )
    if isinstance(obj, (list, tuple)):
        return any(contains_canary_recursive(item, canary) for item in obj)
    return False


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
    """Return True when PreToolUse denials should be logged but not enforced.

    Operator-only soft mode for isolated test environments. The control plane seeds
    ``XIANIX-HARDENING-AUDIT`` from the agent host (``EXECUTOR-HARDENING-AUDIT``) and
    rejects that name from tenant ``with-envs``, so webhook/rules.json authors cannot
    disable enforcement. Default is fail-closed (this returns False).
    """
    raw = (
        os.environ.get("XIANIX_HARDENING_AUDIT")
        or os.environ.get("XIANIX-HARDENING-AUDIT")
        or ""
    )
    return raw.strip().lower() in ("1", "true", "yes")


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
    name = (tool_name or "").strip()

    if contains_canary(name, canary):
        return "blocked: canary value present in tool arguments"

    # Read/Bash only need path/command fields — avoid serializing large payloads.
    if _is_read_like(name):
        path = _tool_path(tool_input)
        if contains_canary(path, canary):
            return "blocked: canary value present in tool arguments"
        if path and not _is_allowed_read_path(path):
            return f"blocked: refusing to read path outside workspace {path}"
        if path and _SECRET_PATH_RE.search(path.replace("\\", "/")):
            return f"blocked: refusing to read secret path {path}"
        return None

    if _is_bash_like(name):
        command = _bash_command(tool_input)
        if contains_canary(command, canary):
            return "blocked: canary value present in tool arguments"

        # Normalize path separators once. Keep the raw command for patterns that
        # match hex escapes (e.g. printf \x2e\x65\x6e\x76) which must not be rewritten.
        normalized_command = command.replace("\\", "/")

        if _ENV_EXFIL_RE.search(command):
            return "blocked: environment or credential access is not permitted"
        if (
            _SECRET_PATH_RE.search(normalized_command)
            or _INDIRECT_SECRET_ACCESS_RE.search(command)
        ):
            return "blocked: secret path access is not permitted"
        if not allow_mutates and _PUSH_RE.search(command):
            return "blocked: git commit/push is not permitted in report-only mode"
        return None

    if contains_canary_recursive(tool_input, canary):
        return "blocked: canary value present in tool arguments"

    return None


def _is_allowed_read_path(path: str) -> bool:
    """Allow relative worktree paths and absolute paths under /workspace/ only.

    Resolves symlinks when possible so a workspace-local link cannot escape to
    ``/etc/passwd``, ``~/.ssh``, etc. Lexical ``..`` / home / absolute-outside
    checks still apply first.
    """
    normalized = path.replace("\\", "/").strip()
    if not normalized:
        return True
    if normalized.startswith("~") or normalized.startswith("-"):
        return False
    if ".." in Path(normalized).parts:
        return False

    if normalized.startswith("/") and not (
        normalized == "/workspace" or normalized.startswith(_WORKSPACE_PREFIX)
    ):
        return False

    try:
        # resolve() collapses symlinks; a /workspace/foo → /etc/passwd link fails below.
        real_str = Path(normalized).resolve().as_posix()
    except (OSError, RuntimeError):
        return False

    # Executor containers are POSIX: enforce the real path stays under /workspace/.
    # On Windows unit-test hosts, resolve() yields drive paths — keep lexical allow.
    if real_str.startswith("/"):
        return real_str == "/workspace" or real_str.startswith(_WORKSPACE_PREFIX)

    return True


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
    escaped = _USER_DATA_CLOSE_RE.sub("</ user_data>", value or "")
    safe_name = (name or "input").replace('"', "'")
    return f'<user_data name="{safe_name}">{escaped}</user_data>'

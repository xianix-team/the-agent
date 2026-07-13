#!/usr/bin/env python3
"""
Executes a Claude Code prompt in an isolated workspace.

Reads configuration from environment variables injected by the control plane:
  TENANT_ID            - identifies the tenant
  EXECUTION_ID         - unique ID for this execution
  WORK_DIR             - absolute path to the workspace (repo worktree or empty directory)
  CLAUDE_CODE_PLUGINS  - JSON array of {"plugin-name", "marketplace"?} descriptors
  PROMPT               - fully-interpolated Claude Code prompt to execute
                         (when XIANIX_INPUTS.platform is set, a short host-context
                         preamble is prepended before Claude Code sees it)
  ANTHROPIC_API_KEY    - Anthropic API key (read automatically by the SDK)

Writes a structured JSON envelope to stdout (see `build_output`).
All progress/debug output goes to stderr so it does not pollute the result stream.
"""
import os
import re
import sys
import json
import time
import asyncio
import traceback

from claude_agent_sdk import (
    query,
    ClaudeAgentOptions,
    AssistantMessage,
    UserMessage,
    SystemMessage,
    ResultMessage,
)

from host_context import prepend_host_context


# ── Logging ──────────────────────────────────────────────────────────────────

_start_time = time.monotonic()


def log(msg: str) -> None:
    elapsed = time.monotonic() - _start_time
    print(f"[executor +{elapsed:6.1f}s] {msg}", file=sys.stderr)


def log_separator(label: str) -> None:
    log(f"── {label} {'─' * max(0, 60 - len(label))}")


# ── Helpers ──────────────────────────────────────────────────────────────────

def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise EnvironmentError(f"Required environment variable '{name}' is missing or empty.")
    return value


def parse_plugins(raw: str) -> list[dict]:
    try:
        plugins = json.loads(raw)
        return [p for p in plugins if isinstance(p, dict)]
    except json.JSONDecodeError:
        return []


def parse_tool_list(raw: str | None) -> list[str]:
    """Split a comma-separated XIANIX-*-TOOLS env into a clean tool-name list."""
    if not raw:
        return []
    return [t.strip() for t in raw.split(",") if t.strip()]


def parse_int_env(name: str) -> int | None:
    """Read a positive int env var; returns None when unset, empty, or non-positive/invalid."""
    raw = os.environ.get(name)
    if not raw:
        return None
    try:
        value = int(raw.strip())
    except ValueError:
        log(f"WARNING: {name}='{raw}' is not an integer — ignoring.")
        return None
    return value if value > 0 else None


def parse_float_env(name: str) -> float | None:
    """Read a positive float env var; returns None when unset, empty, or non-positive/invalid."""
    raw = os.environ.get(name)
    if not raw:
        return None
    try:
        value = float(raw.strip())
    except ValueError:
        log(f"WARNING: {name}='{raw}' is not a number — ignoring.")
        return None
    return value if value > 0 else None


# ── Session reuse (opt-in) ─────────────────────────────────────────────────────
# Back-to-back runs against the same conversation (e.g. PR re-reviews on `synchronize`)
# can resume the prior Claude Code session instead of rebuilding context from scratch.
# The session id is persisted on the tenant volume keyed by the generic `conversation-id`
# input, and Claude's session history is persisted via CLAUDE_CONFIG_DIR (set in
# run_prompt.sh). Off by default (XIANIX_RESUME_SESSIONS) and always best-effort — a
# failed resume falls back to a fresh run so re-reviews can never be broken by a
# stale/missing session.

_SESSION_DIR = "/workspace/repo/xianix-sessions"


def session_key(inputs_raw: str | None) -> str | None:
    """Filename-safe session-resume key from the generic `conversation-id` input, or None.

    `conversation-id` is an opaque value authored in rules.json `use-inputs` (e.g. mapped
    from the payload's PR / work-item id); this executor only sanitises it for use as a
    filename and attaches no meaning to its contents — task-specific input names are
    never read here.
    """
    if not inputs_raw:
        return None
    try:
        data = json.loads(inputs_raw)
    except (json.JSONDecodeError, TypeError):
        return None
    if not isinstance(data, dict):
        return None

    conversation_id = data.get("conversation-id") or ""
    if not conversation_id:
        return None
    return re.sub(r"[^A-Za-z0-9._-]", "_", str(conversation_id))


def read_prior_session(key: str) -> str | None:
    try:
        with open(os.path.join(_SESSION_DIR, key), encoding="utf-8") as fh:
            return fh.read().strip() or None
    except OSError:
        return None


def persist_session(key: str, session_id: str | None) -> None:
    if not session_id:
        return
    try:
        os.makedirs(_SESSION_DIR, exist_ok=True)
        with open(os.path.join(_SESSION_DIR, key), "w", encoding="utf-8") as fh:
            fh.write(session_id)
    except OSError as exc:
        log(f"WARNING: could not persist session id for '{key}': {exc}")


# ── Usage accumulation ─────────────────────────────────────────────────────────
# The SDK only reports an authoritative cost on the final ResultMessage. When a run is
# aborted mid-stream — most notably when `max_budget_usd` is hit, where the SDK raises a
# transport-level error *before* yielding any ResultMessage — that message never arrives,
# so cost/token metrics would be lost entirely. To keep an aborted run's metrics intact we
# also accumulate the per-turn `usage` each AssistantMessage carries: every turn is one
# billed API call, so summing per-call usage gives the true total token spend, and per-model
# splits let the consumer price the run accurately even without the ResultMessage.

# CLI usage keys (per the Anthropic API response shape).
_SDK_USAGE_KEYS = (
    "input_tokens",
    "output_tokens",
    "cache_read_input_tokens",
    "cache_creation_input_tokens",
)

# Map CLI usage keys → the executor envelope's token field names (matching build_output).
_ENVELOPE_USAGE_KEYS = {
    "input_tokens": "input_tokens",
    "output_tokens": "output_tokens",
    "cache_read_input_tokens": "cache_read_tokens",
    "cache_creation_input_tokens": "cache_creation_tokens",
}


def _add_usage(totals: dict[str, int], usage: dict | None) -> None:
    if not isinstance(usage, dict):
        return
    for key in _SDK_USAGE_KEYS:
        value = usage.get(key)
        if isinstance(value, int) and not isinstance(value, bool):
            totals[key] = totals.get(key, 0) + value


class RunState:
    """Mutable accumulator for one query run.

    Lives at module scope and is mutated as messages stream in, so a mid-stream failure
    (e.g. the SDK raising when ``max_budget_usd`` is exceeded, before any ResultMessage is
    emitted) still leaves the partial output / usage / turn data available to the error
    envelope — otherwise cost and token metrics for an aborted run would be lost.

    ``reset()`` runs at the start of every ``collect_messages`` call so the resume-then-fresh
    retry (a failed session resume falling back to a fresh run) can never double-count.
    """

    def __init__(self) -> None:
        self.reset()

    def reset(self) -> None:
        self.text_blocks: list[str] = []
        self.tool_uses: list[dict] = []
        self.models_seen: set[str] = set()
        self.result_message: ResultMessage | None = None
        self.turn_count: int = 0
        self.usage_totals: dict[str, int] = {}
        self.model_usage: dict[str, dict[str, int]] = {}

    def record_assistant_usage(self, model: str | None, usage: dict | None) -> None:
        _add_usage(self.usage_totals, usage)
        if model:
            _add_usage(self.model_usage.setdefault(model, {}), usage)

    def final_usage(self) -> dict:
        """Authoritative ResultMessage usage when present, else the per-turn accumulation."""
        return extract_usage(self.result_message) or self.usage_totals

    def model_usage_envelope(self) -> dict | None:
        """Per-model token totals keyed by the envelope's field names, or None when empty."""
        if not self.model_usage:
            return None
        return {
            model: {env: totals.get(sdk, 0) for sdk, env in _ENVELOPE_USAGE_KEYS.items()}
            for model, totals in self.model_usage.items()
        }


# Module-level so the top-level error handler can still read partial state after an abort.
_run = RunState()


async def collect_messages(prompt: str, options: ClaudeAgentOptions) -> None:
    """Runs one query, accumulating output / tool uses / usage into the module-level `_run`.

    Mutates shared state (rather than returning a dict) so that if the stream raises
    mid-run — e.g. the SDK aborting on a budget cap before any ResultMessage — the partial
    usage and turn data survive for the caller's error envelope. Resets `_run` first so a
    resume-then-fresh retry never double-counts.
    """
    _run.reset()

    async for message in query(prompt=prompt, options=options):
        if isinstance(message, AssistantMessage):
            _run.turn_count += 1
            log(f"[turn {_run.turn_count}] assistant")
            process_assistant_message(message, _run.text_blocks, _run.tool_uses, _run.models_seen)
            _run.record_assistant_usage(getattr(message, "model", None), getattr(message, "usage", None))

        elif isinstance(message, UserMessage):
            log(f"[turn {_run.turn_count}] tool_result")
            process_user_message(message)

        elif isinstance(message, SystemMessage):
            log("[system]")
            process_system_message(message)

        elif isinstance(message, ResultMessage):
            _run.result_message = message
            log_separator("Result")
            process_result_message(message)


def plugin_names(plugins: list[dict]) -> list[str]:
    return [p.get("plugin-name", "<unknown>") for p in plugins]


def extract_usage(result_message: ResultMessage | None) -> dict:
    if result_message is None:
        return {}
    usage = getattr(result_message, "usage", None)
    return usage if isinstance(usage, dict) else {}


def truncate(text: str, max_len: int = 300) -> str:
    text = text.strip()
    if len(text) <= max_len:
        return text
    return text[:max_len] + f"... ({len(text)} chars total)"


def format_tool_input(name: str, tool_input: dict | str) -> str:
    """Extract the most meaningful part of a tool invocation for logging."""
    if isinstance(tool_input, str):
        return truncate(tool_input, 200)
    if not isinstance(tool_input, dict):
        return str(tool_input)[:200]

    if name in ("Bash", "bash"):
        return tool_input.get("command", str(tool_input))[:300]
    if name in ("Read", "read_file"):
        return tool_input.get("file_path") or tool_input.get("path", str(tool_input))
    if name in ("Write", "write_file", "Edit", "edit_file"):
        path = tool_input.get("file_path") or tool_input.get("path", "?")
        return f"{path}"
    if name in ("Search", "Grep", "grep", "search"):
        pattern = tool_input.get("pattern") or tool_input.get("query", "")
        path = tool_input.get("path") or tool_input.get("directory", "")
        return f"'{pattern}' in {path}" if path else f"'{pattern}'"

    return truncate(json.dumps(tool_input, default=str), 200)


# ── Message processing ───────────────────────────────────────────────────────

def process_assistant_message(
    message: AssistantMessage,
    text_blocks: list[str],
    tool_uses: list[dict],
    models_seen: set[str] | None = None,
) -> None:
    model = getattr(message, "model", None)
    if model:
        log(f"  model: {model}")
        if models_seen is not None:
            models_seen.add(model)

    for block in message.content:
        block_type = type(block).__name__

        if hasattr(block, "thinking"):
            thinking = getattr(block, "thinking", "")
            if thinking:
                log(f"  thinking: {truncate(thinking, 200)}")

        elif hasattr(block, "text"):
            text = block.text.strip()
            if text:
                text_blocks.append(text)
                preview = truncate(text, 150)
                log(f"  text: {preview}")

        elif hasattr(block, "name"):
            tool_input = getattr(block, "input", {})
            formatted = format_tool_input(block.name, tool_input)
            tool_uses.append({
                "tool": block.name,
                "input_preview": str(tool_input)[:200],
            })
            log(f"  ▶ {block.name}: {formatted}")

        else:
            log(f"  {block_type}: {str(block)[:150]}")


def process_user_message(message: UserMessage) -> None:
    content = message.content if isinstance(message.content, list) else [message.content]
    for block in content:
        if isinstance(block, str):
            if block.strip():
                log(f"  user: {truncate(block, 150)}")
            continue

        block_type = type(block).__name__

        is_error = getattr(block, "is_error", False)
        tool_use_id = getattr(block, "tool_use_id", None)

        if hasattr(block, "content"):
            result_content = block.content
            if isinstance(result_content, list):
                result_text = " ".join(
                    getattr(b, "text", str(b)) for b in result_content
                )
            else:
                result_text = str(result_content)

            status = "✗ error" if is_error else "✓"
            log(f"  {status} result: {truncate(result_text, 200)}")

        elif block_type not in ("str",):
            log(f"  {block_type}: {str(block)[:150]}")


def process_system_message(message: SystemMessage) -> None:
    data = getattr(message, "data", None) or {}

    session_id = getattr(message, "session_id", None) or data.get("session_id")
    if session_id:
        log(f"  session: {session_id}")

    model = getattr(message, "model", None) or data.get("model")
    if model:
        log(f"  model: {model}")

    mcp_servers = getattr(message, "mcp_servers", None) or data.get("mcp_servers")
    if mcp_servers:
        log(f"  mcp_servers: {mcp_servers}")

    content = getattr(message, "content", None)
    if content and isinstance(content, str):
        log(f"  system: {truncate(content, 200)}")


def process_result_message(message: ResultMessage) -> None:
    usage = extract_usage(message)
    cost = getattr(message, "total_cost_usd", None)
    cost_str = f"${cost:.4f}" if cost is not None else "n/a"

    log(
        f"  subtype={message.subtype} cost={cost_str} "
        f"tokens(in={usage.get('input_tokens', 0)} "
        f"out={usage.get('output_tokens', 0)} "
        f"cache_read={usage.get('cache_read_input_tokens', 0)} "
        f"cache_create={usage.get('cache_creation_input_tokens', 0)})"
    )

    model_usage = getattr(message, "model_usage", None) or getattr(message, "modelUsage", None)
    if isinstance(model_usage, dict) and model_usage:
        for model_name, stats in model_usage.items():
            log(f"  model_usage[{model_name}]: {stats}")


# ── Output ───────────────────────────────────────────────────────────────────

def build_output(
    *,
    tenant_id: str,
    execution_id: str,
    plugins: list[dict],
    status: str,
    result: str | None = None,
    tool_uses: list[dict] | None = None,
    duration_seconds: float | None = None,
    cost_usd: float | None = None,
    session_id: str | None = None,
    usage: dict | None = None,
    error: str | None = None,
    error_traceback: str | None = None,
    models: list[str] | None = None,
    model_usage: dict | None = None,
) -> dict:
    """
    Consistent JSON envelope for both success and error cases.
    The C# consumer reads: status, result, cost_usd, session_id,
    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, model_usage.

    Token/usage fields are populated on error envelopes too (e.g. a budget abort), so an
    aborted run's metrics aren't lost. `model_usage` carries the per-model token split the
    consumer uses to estimate cost when no authoritative `cost_usd` is available.
    """
    usage = usage or {}
    return {
        "tenant_id": tenant_id,
        "execution_id": execution_id,
        "plugins": plugin_names(plugins),
        "status": status,
        "result": result,
        "tool_uses": tool_uses,
        "duration_seconds": round(duration_seconds, 2) if duration_seconds else None,
        "cost_usd": cost_usd,
        "session_id": session_id,
        "models": models,
        "input_tokens": usage.get("input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "cache_read_tokens": usage.get("cache_read_input_tokens"),
        "cache_creation_tokens": usage.get("cache_creation_input_tokens"),
        "model_usage": model_usage,
        "error": error,
        "error_traceback": error_traceback,
    }


def emit(output: dict) -> None:
    json.dump(output, sys.stdout)
    print(file=sys.stdout)
    sys.stdout.flush()


# ── Main ─────────────────────────────────────────────────────────────────────

async def main() -> None:
    tenant_id    = require_env("TENANT_ID")
    execution_id = os.environ.get("EXECUTION_ID", "unknown")
    prompt       = require_env("PROMPT")
    work_dir     = os.environ.get("WORK_DIR", "/workspace")
    plugins      = parse_plugins(os.environ.get("CLAUDE_CODE_PLUGINS", "[]"))

    # When rules.json declares platform, surface it in the prompt so free-form plugin
    # runs (comment instructions, etc.) pick the right host APIs. Env alone is easy to miss.
    prompt = prepend_host_context(prompt, os.environ.get("XIANIX_INPUTS"))

    # ── Cost-control levers (all optional; injected by the control plane) ─────
    # Primary model: XIANIX_MODEL (first-class rules.json `model`) wins, then a raw
    # ANTHROPIC_MODEL passthrough (the zero-code with-envs path), else the SDK default.
    model            = os.environ.get("XIANIX_MODEL") or os.environ.get("ANTHROPIC_MODEL") or None
    # Per-execution max-turns wins; otherwise fall back to the host-wide backstop (off unless set).
    max_turns        = parse_int_env("XIANIX_MAX_TURNS") or parse_int_env("XIANIX_DEFAULT_MAX_TURNS")
    allowed_tools    = parse_tool_list(os.environ.get("XIANIX_ALLOWED_TOOLS"))
    disallowed_tools = parse_tool_list(os.environ.get("XIANIX_DISALLOWED_TOOLS"))
    max_budget_usd   = parse_float_env("XIANIX_MAX_BUDGET_USD") or 10.0

    # Route Claude Code's background/side-query work (titles, summaries, etc.) to a cheap
    # Haiku-class model unless the operator already pinned one. ANTHROPIC_DEFAULT_HAIKU_MODEL
    # is the current control; ANTHROPIC_SMALL_FAST_MODEL is its deprecated alias, set too for
    # older CLI builds. setdefault keeps any explicit override intact.
    os.environ.setdefault("ANTHROPIC_DEFAULT_HAIKU_MODEL", "claude-haiku-4-5")
    os.environ.setdefault("ANTHROPIC_SMALL_FAST_MODEL", "claude-haiku-4-5")

    log_separator("Configuration")
    log(f"tenant={tenant_id} execution={execution_id}")
    log(f"work_dir={work_dir}")
    log(f"plugins={plugin_names(plugins)}")
    log(f"ANTHROPIC_API_KEY={'set' if os.environ.get('ANTHROPIC_API_KEY') else 'MISSING'}")
    log(f"model={model or '(sdk default)'} max_turns={max_turns or '(none)'} "
        f"allowed_tools={allowed_tools or '(all)'} disallowed_tools={disallowed_tools or '(none)'} "
        f"max_budget_usd={max_budget_usd if max_budget_usd is not None else '(none)'}")
    log(f"haiku_model={os.environ.get('ANTHROPIC_DEFAULT_HAIKU_MODEL')}")

    log_separator("Prompt")
    log(f"prompt_length={len(prompt)} chars, {len(prompt.splitlines())} lines")
    print("┌──────────────────────── PROMPT ────────────────────────", file=sys.stderr)
    for line in prompt.splitlines() or [""]:
        print(f"│ {line}", file=sys.stderr)
    print("└────────────────────────────────────────────────────────", file=sys.stderr)
    sys.stderr.flush()

    # Build options from only the levers that were actually set, so an unset lever falls back
    # to the SDK's own default rather than forcing an empty/zero value onto it.
    option_kwargs: dict = {
        "cwd": work_dir,
        "permission_mode": "bypassPermissions",
    }
    if model:
        option_kwargs["model"] = model
    if max_turns is not None:
        option_kwargs["max_turns"] = max_turns
    if allowed_tools:
        option_kwargs["allowed_tools"] = allowed_tools
    if disallowed_tools:
        option_kwargs["disallowed_tools"] = disallowed_tools
    if max_budget_usd is not None:
        option_kwargs["max_budget_usd"] = max_budget_usd

    # Opt-in session resume: only when explicitly enabled and a prior session exists for this
    # conversation (repo + PR/issue). Best-effort — a resume failure retries a fresh run.
    resume_enabled = os.environ.get("XIANIX_RESUME_SESSIONS", "").strip().lower() in ("1", "true", "yes")
    skey = session_key(os.environ.get("XIANIX_INPUTS")) if resume_enabled else None
    prior_sid = read_prior_session(skey) if skey else None
    log(f"session_resume={'on' if resume_enabled else 'off'} key={skey or '(none)'} "
        f"prior_session={prior_sid or '(none)'}")

    log_separator("Execution")

    if prior_sid:
        try:
            log(f"Resuming session {prior_sid}.")
            await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs, resume=prior_sid))
        except Exception as exc:  # noqa: BLE001 — resume must never harden into a hard failure
            log(f"WARNING: session resume failed ({type(exc).__name__}: {exc}) — retrying fresh.")
            await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs))
    else:
        await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs))

    if skey:
        persist_session(skey, getattr(_run.result_message, "session_id", None))

    duration = time.monotonic() - _start_time

    log_separator("Summary")
    log(f"turns={_run.turn_count} text_blocks={len(_run.text_blocks)} "
        f"tool_uses={len(_run.tool_uses)} duration={duration:.1f}s")
    if _run.models_seen:
        log(f"models={sorted(_run.models_seen)}")

    emit(build_output(
        tenant_id=tenant_id,
        execution_id=execution_id,
        plugins=plugins,
        status="completed",
        result="\n\n".join(_run.text_blocks) if _run.text_blocks else None,
        tool_uses=_run.tool_uses or None,
        duration_seconds=duration,
        cost_usd=getattr(_run.result_message, "total_cost_usd", None),
        session_id=getattr(_run.result_message, "session_id", None),
        usage=_run.final_usage(),
        models=sorted(_run.models_seen) if _run.models_seen else None,
        model_usage=_run.model_usage_envelope(),
    ))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except BaseException as e:  # noqa: BLE001
        duration = time.monotonic() - _start_time
        log(f"fatal: {type(e).__name__}: {e} (after {duration:.1f}s)")

        plugins = parse_plugins(os.environ.get("CLAUDE_CODE_PLUGINS", "[]"))
        # Carry whatever was accumulated before the abort (most importantly token usage when
        # a budget cap is hit) so cost/token metrics survive into the error envelope instead
        # of being reported as null.
        emit(build_output(
            tenant_id=os.environ.get("TENANT_ID", "unknown"),
            execution_id=os.environ.get("EXECUTION_ID", "unknown"),
            plugins=plugins,
            status="error",
            result="\n\n".join(_run.text_blocks) if _run.text_blocks else None,
            tool_uses=_run.tool_uses or None,
            duration_seconds=duration,
            cost_usd=getattr(_run.result_message, "total_cost_usd", None),
            session_id=getattr(_run.result_message, "session_id", None),
            usage=_run.final_usage(),
            models=sorted(_run.models_seen) if _run.models_seen else None,
            model_usage=_run.model_usage_envelope(),
            error=f"{type(e).__name__}: {e}",
            error_traceback=traceback.format_exc(),
        ))
        sys.exit(1)

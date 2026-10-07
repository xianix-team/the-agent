#!/usr/bin/env python3
"""
Execute a prompt via the OpenCode CLI inside the executor workspace.

Entry: ``python3 execute_opencode.py`` (called from run_prompt.sh).
Invokes:
  opencode run --auto --print-logs --format json --dir <workspace> [--model <provider/model>] <prompt>

Maps JSONL events into the stdout JSON envelope for the control plane.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
import traceback

from plugin_runtime import format_version_logs, prepare_plugins_or_raise
from host_context import prepend_host_context


_start_time = time.monotonic()


def log(msg: str) -> None:
    elapsed = time.monotonic() - _start_time
    print(f"[executor-opencode +{elapsed:6.1f}s] {msg}", file=sys.stderr)


def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise EnvironmentError(f"Required environment variable '{name}' is missing or empty.")
    return value


def resolve_opencode_model(raw: str | None) -> str | None:
    """Require provider/model from rules.json when set (no inventing)."""
    if not raw:
        return None
    model = raw.strip()
    if not model:
        return None
    if not re.fullmatch(r"[a-zA-Z0-9_-]+/[a-zA-Z0-9._-]+", model):
        raise ValueError(
            f"OpenCode model must be 'provider/model' "
            f"(e.g. openai/gpt-5.3-codex), got '{model}'."
        )
    return model


def require_credentials_for_model(model: str | None) -> None:
    if not model:
        if not os.environ.get("OPENAI_API_KEY") and not os.environ.get("ANTHROPIC_API_KEY"):
            raise ValueError(
                "OpenCode requires OPENAI_API_KEY or ANTHROPIC_API_KEY "
                "(or set model in rules.json with the matching secret)."
            )
        return
    provider = model.split("/", 1)[0].lower()
    if provider in ("openai", "oai"):
        if not os.environ.get("OPENAI_API_KEY"):
            raise ValueError(
                "OpenCode openai/* requires OPENAI_API_KEY "
                "(inject via with-envs secrets.OPENAI-API-KEY)."
            )
        return
    if provider in ("anthropic", "claude"):
        if not os.environ.get("ANTHROPIC_API_KEY"):
            raise ValueError(
                "OpenCode anthropic/* requires ANTHROPIC_API_KEY."
            )


def build_output(
    *,
    tenant_id: str,
    execution_id: str,
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
) -> dict:
    usage = usage or {}
    return {
        "tenant_id": tenant_id,
        "execution_id": execution_id,
        "plugins": [],
        "status": status,
        "harness": "opencode",
        "result": result,
        "tool_uses": tool_uses,
        "duration_seconds": round(duration_seconds, 2) if duration_seconds else None,
        "cost_usd": cost_usd,
        "session_id": session_id,
        "models": models,
        "input_tokens": usage.get("input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "cache_read_tokens": usage.get("cache_read_tokens"),
        "cache_creation_tokens": usage.get("cache_creation_tokens"),
        "model_usage": None,
        "error": error,
        "error_traceback": error_traceback,
    }


def emit(output: dict) -> None:
    json.dump(output, sys.stdout)
    print(file=sys.stdout)
    sys.stdout.flush()


def extract_opencode_log_error(stderr: str) -> str | None:
    """Pull the real cause from --print-logs when JSONL only has Unexpected server error."""
    if not stderr:
        return None
    # Prefer ProviderModelNotFoundError / AI_APICallError lines over generic UnknownError.
    preferred: list[str] = []
    fallback: list[str] = []
    for line in stderr.splitlines():
        if "ProviderModelNotFoundError" in line or "AI_APICallError" in line:
            preferred.append(line.strip())
        elif 'level=ERROR' in line and "Unexpected server error" not in line:
            fallback.append(line.strip())
    for line in preferred or fallback:
        # Common shapes: error="..." or error.error="..."
        for marker in ('error.error="', 'error="', "error.error='", "error='"):
            idx = line.find(marker)
            if idx >= 0:
                rest = line[idx + len(marker) :]
                end = rest.find('"') if marker.endswith('"') else rest.find("'")
                if end > 0:
                    return rest[:end][:500]
        if "ProviderModelNotFoundError:" in line:
            return line.split("ProviderModelNotFoundError:", 1)[1].strip()[:500]
        if "AI_APICallError:" in line:
            return line.split("AI_APICallError:", 1)[1].strip()[:500]
    return None


def parse_jsonl_events(
    raw: str,
) -> tuple[list[str], list[dict], str | None, float | None, dict, str | None]:
    """Accumulate text / tools / session / cost / tokens / last error from opencode JSONL."""
    text_blocks: list[str] = []
    tool_uses: list[dict] = []
    session_id: str | None = None
    cost_usd: float | None = None
    usage: dict = {}
    last_error: str | None = None

    for line in raw.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            log(f"WARNING: skipping non-JSON stdout line: {line[:120]}")
            continue

        if not isinstance(event, dict):
            continue

        etype = event.get("type")
        if event.get("sessionID"):
            session_id = str(event["sessionID"])

        part = event.get("part") if isinstance(event.get("part"), dict) else {}

        if etype == "text":
            text = part.get("text") or event.get("text")
            if isinstance(text, str) and text.strip():
                text_blocks.append(text.strip())
                log(f"  text: {text.strip()[:150]}")

        elif etype == "tool_use":
            tool_name = part.get("tool") or event.get("tool") or "unknown"
            state = part.get("state") if isinstance(part.get("state"), dict) else {}
            tool_input = state.get("input") if isinstance(state.get("input"), dict) else {}
            tool_uses.append({
                "tool": tool_name,
                "input_preview": str(tool_input)[:200],
            })
            log(f"  ▶ {tool_name}: {str(tool_input)[:200]}")

        elif etype == "step_finish":
            tokens = part.get("tokens") if isinstance(part.get("tokens"), dict) else {}
            if tokens:
                usage = {
                    "input_tokens": tokens.get("input"),
                    "output_tokens": tokens.get("output"),
                    "cache_read_tokens": (tokens.get("cache") or {}).get("read")
                    if isinstance(tokens.get("cache"), dict) else None,
                    "cache_creation_tokens": (tokens.get("cache") or {}).get("write")
                    if isinstance(tokens.get("cache"), dict) else None,
                }
            cost = part.get("cost")
            if isinstance(cost, (int, float)):
                cost_usd = float(cost)
            log(f"  step_finish reason={part.get('reason')} cost={cost_usd} tokens={tokens}")

        elif etype == "error":
            err = event.get("error") if isinstance(event.get("error"), dict) else {}
            msg = None
            if isinstance(err.get("data"), dict):
                msg = err["data"].get("message")
            msg = msg or err.get("message") or err.get("name")
            if not msg and isinstance(event.get("error"), str):
                msg = event["error"]
            msg = msg or str(event)
            last_error = str(msg)[:500]
            log(f"  error event: {last_error}")

        elif etype == "step_start":
            log("  step_start")

    return text_blocks, tool_uses, session_id, cost_usd, usage, last_error


def run(
    *,
    prompt: str,
    model: str | None,
    workspace: str,
    tenant_id: str,
    execution_id: str,
    xianix_inputs: str | None = None,
    provisioned_runtimes: str | None = None,
    opencode_config_dir: str | None = None,
) -> dict:
    """
    Run OpenCode against the workspace and return the control-plane envelope.
    """
    try:
        # Install harness-native .opencode/ overlays for requested use-plugins.
        os.environ.setdefault("WORK_DIR", workspace)
        plugin_info = prepare_plugins_or_raise(prompt)
    except (FileNotFoundError, RuntimeError, ValueError, json.JSONDecodeError) as e:
        return build_output(
            tenant_id=tenant_id,
            execution_id=execution_id,
            status="error",
            models=[model] if model else None,
            error=str(e),
        )

    prompt = prepend_host_context(
        prompt,
        xianix_inputs,
        provisioned_runtimes,
    )
    try:
        resolved_model = resolve_opencode_model(model)
        require_credentials_for_model(resolved_model)
    except ValueError as e:
        return build_output(
            tenant_id=tenant_id,
            execution_id=execution_id,
            status="error",
            models=[model] if model else None,
            error=str(e),
        )

    if plugin_info:
        for line in format_version_logs(plugin_info, resolved_model):
            log(line)

    opencode_bin = shutil.which("opencode")
    if not opencode_bin:
        return build_output(
            tenant_id=tenant_id,
            execution_id=execution_id,
            status="error",
            models=[resolved_model] if resolved_model else None,
            error="opencode CLI not found on PATH. Install opencode-ai in the executor image.",
        )

    cmd = [
        opencode_bin,
        "run",
        "--auto",
        "--print-logs",
        "--log-level", "WARN",
        "--format", "json",
        "--dir", workspace,
    ]
    if resolved_model:
        cmd.extend(["--model", resolved_model])
    cmd.append(prompt)

    log(f"tenant={tenant_id} execution={execution_id}")
    log(f"work_dir={workspace}")
    log(f"model={resolved_model or '(opencode default)'}")
    log(f"OPENAI_API_KEY={'set' if os.environ.get('OPENAI_API_KEY') else 'MISSING'}")
    log(f"ANTHROPIC_API_KEY={'set' if os.environ.get('ANTHROPIC_API_KEY') else 'MISSING'}")
    log(
        f"cmd: opencode run --auto --format json --dir {workspace}"
        f"{f' --model {resolved_model}' if resolved_model else ''} <prompt {len(prompt)} chars>"
    )

    if opencode_config_dir:
        os.makedirs(opencode_config_dir, exist_ok=True)
        os.environ["OPENCODE_CONFIG_DIR"] = opencode_config_dir
        log(f"OPENCODE_CONFIG_DIR={opencode_config_dir}")
    else:
        repo_dir = "/workspace/repo"
        if os.path.isdir(repo_dir):
            oc_config = os.path.join(repo_dir, "xianix-opencode-config")
            os.makedirs(oc_config, exist_ok=True)
            os.environ.setdefault("OPENCODE_CONFIG_DIR", oc_config)
            log(f"OPENCODE_CONFIG_DIR={oc_config}")

    os.environ.setdefault("OPENCODE_DISABLE_AUTOUPDATE", "1")

    try:
        proc = subprocess.run(
            cmd,
            cwd=workspace,
            capture_output=True,
            text=True,
            check=False,
        )
    except BaseException as e:  # noqa: BLE001
        duration = time.monotonic() - _start_time
        log(f"fatal: {type(e).__name__}: {e} (after {duration:.1f}s)")
        return build_output(
            tenant_id=tenant_id,
            execution_id=execution_id,
            status="error",
            duration_seconds=duration,
            models=[resolved_model] if resolved_model else None,
            error=f"{type(e).__name__}: {e}",
            error_traceback=traceback.format_exc(),
        )

    if proc.stderr:
        for line in proc.stderr.splitlines():
            log(f"[opencode stderr] {line}")

    text_blocks, tool_uses, session_id, cost_usd, usage, last_error = parse_jsonl_events(
        proc.stdout or ""
    )
    duration = time.monotonic() - _start_time

    if proc.returncode != 0:
        err = last_error or f"opencode exited {proc.returncode}"
        log_err = extract_opencode_log_error(proc.stderr or "")
        if log_err and (
            not last_error
            or "Unexpected server error" in last_error
            or last_error.startswith("opencode exited")
        ):
            err = log_err
            if proc.returncode:
                err = f"{err} (opencode exited {proc.returncode})"
        elif not last_error and proc.stderr:
            last_line = proc.stderr.strip().rsplit("\n", 1)[-1][:300]
            err = f"{err}: {last_line}"
        elif last_error and proc.returncode:
            err = f"{last_error} (opencode exited {proc.returncode})"
        return build_output(
            tenant_id=tenant_id,
            execution_id=execution_id,
            status="error",
            result="\n\n".join(text_blocks) if text_blocks else None,
            tool_uses=tool_uses or None,
            duration_seconds=duration,
            cost_usd=cost_usd,
            session_id=session_id,
            usage=usage,
            models=[resolved_model] if resolved_model else None,
            error=err,
        )

    return build_output(
        tenant_id=tenant_id,
        execution_id=execution_id,
        status="completed",
        result="\n\n".join(text_blocks) if text_blocks else (proc.stdout or None),
        tool_uses=tool_uses or None,
        duration_seconds=duration,
        cost_usd=cost_usd,
        session_id=session_id,
        usage=usage,
        models=[resolved_model] if resolved_model else None,
    )


if __name__ == "__main__":
    _standalone = run(
        prompt=require_env("PROMPT"),
        model=os.environ.get("XIANIX_MODEL") or None,
        workspace=os.environ.get("WORK_DIR", "/workspace"),
        tenant_id=require_env("TENANT_ID"),
        execution_id=os.environ.get("EXECUTION_ID", "unknown"),
        xianix_inputs=os.environ.get("XIANIX_INPUTS"),
        provisioned_runtimes=os.environ.get("XIANIX_PROVISIONED_RUNTIMES"),
    )
    emit(_standalone)
    if _standalone.get("status") != "completed":
        sys.exit(1)

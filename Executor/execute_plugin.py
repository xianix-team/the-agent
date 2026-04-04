#!/usr/bin/env python3
"""
Executes a Claude Code prompt against the cloned repository.

Reads configuration from environment variables injected by the control plane:
  TENANT_ID            - identifies the tenant
  CLAUDE_CODE_PLUGINS  - JSON array of {name, url} for Claude Code plugins installed by entrypoint.sh
  PROMPT               - fully-interpolated Claude Code prompt to execute
  ANTHROPIC_API_KEY    - Anthropic API key (read automatically by the SDK from the environment)

Writes the structured JSON result to stdout.
All progress/debug output goes to stderr so it does not pollute the result stream.
"""
import os
import sys
import json
import asyncio
import traceback

from claude_code_sdk import query, ClaudeCodeOptions, AssistantMessage, ResultMessage


async def main() -> None:
    tenant_id           = os.environ["TENANT_ID"]
    prompt              = os.environ["PROMPT"]
    claude_code_plugins = os.environ.get("CLAUDE_CODE_PLUGINS", "[]")
    repo_path           = "/workspace/repo"

    try:
        plugins = json.loads(claude_code_plugins)
    except json.JSONDecodeError:
        plugins = []

    print(f"[executor] tenant={tenant_id}", file=sys.stderr)
    print(f"[executor] claude-code-plugins={[p.get('name') for p in plugins]}", file=sys.stderr)
    print(f"[executor] prompt (first 120 chars)={prompt[:120]}", file=sys.stderr)
    print(f"[executor] ANTHROPIC_API_KEY set={bool(os.environ.get('ANTHROPIC_API_KEY'))}", file=sys.stderr)

    text_blocks: list[str] = []
    result_message: ResultMessage | None = None

    options = ClaudeCodeOptions(
        cwd=repo_path,
        # Bypass all permission prompts — the container itself is the security boundary.
        permission_mode="bypassPermissions",
    )

    async for message in query(prompt=prompt, options=options):
        print(f"[executor] message type: {type(message).__name__}", file=sys.stderr)

        if isinstance(message, AssistantMessage):
            for block in message.content:
                block_type = type(block).__name__
                if hasattr(block, "text"):
                    text_blocks.append(block.text)
                    print(f"[executor] text ({block_type}): {block.text[:300]}", file=sys.stderr)
                elif hasattr(block, "name"):
                    print(f"[executor] tool_use ({block_type}): {block.name}({str(getattr(block, 'input', ''))[:150]})", file=sys.stderr)
                else:
                    print(f"[executor] block ({block_type}): {str(block)[:150]}", file=sys.stderr)

        elif isinstance(message, ResultMessage):
            result_message = message
            print(f"[executor] result: subtype={message.subtype} cost={getattr(message, 'total_cost_usd', 'n/a')}", file=sys.stderr)

    output = {
        "tenant_id": tenant_id,
        "claude_code_plugins": [p.get("name") for p in plugins],
        "status": "completed",
        "result": "\n\n".join(text_blocks),
        "cost_usd": getattr(result_message, "total_cost_usd", None),
        "session_id": getattr(result_message, "session_id", None),
    }

    json.dump(output, sys.stdout, indent=2)
    sys.stdout.flush()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except BaseException as e:  # noqa: BLE001 — catches SystemExit/CancelledError from the Claude CLI subprocess
        error_output = {
            "status": "error",
            "error": str(e),
            "traceback": traceback.format_exc(),
        }
        json.dump(error_output, sys.stdout, indent=2)
        sys.stdout.flush()
        sys.exit(1)

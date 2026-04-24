#!/usr/bin/env bash
# Container entrypoint. Dispatches to the prepare/run scripts based on XIANIX_MODE.
#
#   prepare              — bare-clone the repo into the tenant volume; no plugins,
#                          no prompt. Used by the chat-driven OnboardRepository
#                          tool to add a new repo without running anything.
#   execute              — assume the workspace is ready; install plugins and run
#                          the prompt. (Reserved for future use; today nothing in
#                          C# emits this mode.)
#   prepare-and-execute  — DEFAULT. Today's behaviour: clone-or-fetch + worktree
#                          + plugins + prompt + cleanup. Used by every webhook
#                          flow and by RunClaudeCodeOnRepository.
set -euo pipefail

MODE="${XIANIX_MODE:-prepare-and-execute}"

case "${MODE}" in
    prepare)
        exec /workspace/prepare_repo.sh
        ;;
    execute)
        exec /workspace/run_prompt.sh
        ;;
    prepare-and-execute)
        /workspace/prepare_repo.sh && exec /workspace/run_prompt.sh
        ;;
    *)
        echo "FATAL: unknown XIANIX_MODE='${MODE}' (expected: prepare | execute | prepare-and-execute)" >&2
        exit 1
        ;;
esac

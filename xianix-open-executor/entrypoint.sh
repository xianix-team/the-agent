#!/usr/bin/env bash
# Container entrypoint. Dispatches prepare/run based on XIANIX_MODE.
#
#   prepare              — bare-clone only (OnboardRepository)
#   execute              — run OpenCode against an existing workspace
#   prepare-and-execute  — DEFAULT: clone/fetch + worktree + OpenCode + cleanup
set -euo pipefail

MODE="${XIANIX_MODE:-$(printenv 'XIANIX-MODE' || true)}"
MODE="${MODE:-prepare-and-execute}"

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

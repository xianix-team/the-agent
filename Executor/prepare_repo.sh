#!/usr/bin/env bash
# Prepare phase of the executor pipeline.
#
# Behaviour depends on XIANIX_MODE (default: prepare-and-execute):
#   * prepare              — bare clone only; no worktree, no plugins, no prompt.
#                            Used by the chat-driven OnboardRepository flow.
#   * prepare-and-execute  — bare clone + worktree (so run_prompt.sh can cd into
#                            it). Used by webhook flows and the chat
#                            RunClaudeCodeOnRepository flow.
#   * (any other value)    — caller is misusing this script; we still do the
#                            prepare-and-execute path for safety.
#
# When REPOSITORY_URL is empty we either skip (mode=prepare) or create an empty
# workspace directory so a no-repo prompt run still has somewhere to cd into.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=_common.sh
source "${SCRIPT_DIR}/_common.sh"

XIANIX_MODE="${XIANIX_MODE:-prepare-and-execute}"

log "=== Xianix Executor — prepare phase ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Mode:                ${XIANIX_MODE}"
log "Repository:          ${REPOSITORY_URL:-<none>}"
log "Platform:            ${PLATFORM:-<none>}"
[ -n "${GIT_REF}" ] && log "Git ref:             ${GIT_REF}"

ensure_bare_repo() {
    if [ -d "${REPO_DIR}" ] && { [ -d "${REPO_DIR}/.git" ] || [ -f "${REPO_DIR}/HEAD" ]; }; then
        log "--- Fetching into existing repo ---"
        if ! git -C "${REPO_DIR}" fetch --all --prune >&2; then
            log "--- Fetch failed, re-cloning ---"
            find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
            git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2
        fi
    else
        log "--- Cloning repository (bare, first run for this tenant+repo) ---"
        find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2
    fi

    git -C "${REPO_DIR}" worktree prune >&2 2>/dev/null || true
}

create_worktree() {
    if [ -n "${GIT_REF}" ]; then
        log "--- Creating worktree for ref: ${GIT_REF} ---"
        git -C "${REPO_DIR}" fetch origin "${GIT_REF}" >&2
        git -C "${REPO_DIR}" worktree add "${WORK_DIR}" FETCH_HEAD --detach >&2
    else
        log "--- Creating worktree for HEAD ---"
        git -C "${REPO_DIR}" worktree add "${WORK_DIR}" HEAD --detach >&2
    fi
}

prepare_empty_workspace() {
    log "--- No repository — creating empty workspace ---"
    mkdir -p "${WORK_DIR}"
}

if [ -n "${REPOSITORY_URL}" ]; then
    configure_credentials
    ensure_bare_repo

    if [ "${XIANIX_MODE}" = "prepare" ]; then
        log "--- Skipping worktree (mode=prepare; bare clone only) ---"
    else
        create_worktree
    fi
else
    if [ "${XIANIX_MODE}" = "prepare" ]; then
        log "--- No repository and mode=prepare; nothing to onboard ---"
    else
        prepare_empty_workspace
    fi
fi

log "--- Prepare phase complete ---"

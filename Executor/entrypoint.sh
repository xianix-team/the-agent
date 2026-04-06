#!/usr/bin/env bash
set -euo pipefail

log() { echo "$@" >&2; }

log "=== Xianix Executor ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Claude Code Plugins: ${CLAUDE_CODE_PLUGINS}"

# ── Extract dynamic inputs ──────────────────────────────────────────────────
REPOSITORY_URL=$(echo "${XIANIX_INPUTS}" | jq -r '."repository-url" // empty')
PLATFORM=$(echo "${XIANIX_INPUTS}"       | jq -r '.platform // "github"')
GIT_REF=$(echo "${XIANIX_INPUTS}"        | jq -r '."pr-head-branch" // empty')

if [ -z "${REPOSITORY_URL}" ]; then
    log "FATAL: 'repository-url' is missing or empty in XIANIX_INPUTS"
    exit 1
fi

log "Repository:          ${REPOSITORY_URL}"
log "Platform:            ${PLATFORM}"
[ -n "${GIT_REF}" ] && log "Git ref:             ${GIT_REF}"

# ── Configure git credentials ────────────────────────────────────────────────
if [ -n "${GITHUB_TOKEN:-}" ]; then
    git config --global url."https://${GITHUB_TOKEN}@github.com/".insteadOf "https://github.com/" >&2 2>&1
    export GH_TOKEN="${GITHUB_TOKEN}"
fi
if [ "${PLATFORM}" = "azuredevops" ] && [ -n "${AZURE_DEVOPS_TOKEN:-}" ]; then
    git config --global url."https://pat:${AZURE_DEVOPS_TOKEN}@dev.azure.com/".insteadOf "https://dev.azure.com/" >&2 2>&1
fi

# ── Bare repo (shared object store on the persistent volume) ─────────────────
REPO_DIR="/workspace/repo"
WORK_DIR="/workspace/exec-${EXECUTION_ID}"

if [ -d "${REPO_DIR}" ] && { [ -d "${REPO_DIR}/.git" ] || [ -f "${REPO_DIR}/HEAD" ]; }; then
    log "--- Fetching into existing repo ---"
    if ! git -C "${REPO_DIR}" fetch --all --prune >&2 2>&1; then
        log "--- Fetch failed, re-cloning ---"
        find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2 2>&1
    fi
else
    log "--- Cloning repository (bare, first run for this tenant+repo) ---"
    find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2 2>&1
fi

# Clean up orphaned worktrees from previously crashed containers.
git -C "${REPO_DIR}" worktree prune >&2 2>/dev/null || true

# ── Create an isolated worktree for this execution ──────────────────────────
if [ -n "${GIT_REF:-}" ]; then
    log "--- Creating worktree for ref: ${GIT_REF} ---"
    git -C "${REPO_DIR}" fetch origin "${GIT_REF}" >&2 2>&1
    git -C "${REPO_DIR}" worktree add "${WORK_DIR}" FETCH_HEAD --detach >&2 2>&1
else
    log "--- Creating worktree for HEAD ---"
    git -C "${REPO_DIR}" worktree add "${WORK_DIR}" HEAD --detach >&2 2>&1
fi

cd "${WORK_DIR}"
log "--- Worktree ready at ${WORK_DIR} ---"

# ── Install Claude Code plugins ─────────────────────────────────────────────
# Each entry is a JSON object: { name, url, marketplace? }
#
#   url         — plugin reference in `plugin-name@marketplace-name` format passed to
#                 `claude plugin install` (e.g. `code-review@claude-plugins-official`)
#   marketplace — optional source for `claude plugin marketplace add` before installing.
#                 Each unique marketplace is registered only once (deduplication).
#
# Failures on individual plugins are non-fatal — the prompt may still succeed with
# partial tooling.
if [ -n "${CLAUDE_CODE_PLUGINS:-}" ] && [ "${CLAUDE_CODE_PLUGINS}" != "[]" ]; then
    log "--- Installing Claude Code plugins ---"

    # Pass 1: register each unique marketplace once
    echo "${CLAUDE_CODE_PLUGINS}" | jq -r '.[].marketplace // empty' | sort -u | while IFS= read -r mkt; do
        [ -z "${mkt}" ] && continue
        [ "${mkt}" = "anthropics/claude-plugins-official" ] && continue
        if claude plugin marketplace list 2>/dev/null | grep -qF "${mkt}"; then
            log "  Marketplace '${mkt}' already registered — skipping"
        else
            log "  Registering marketplace '${mkt}'"
            claude plugin marketplace add "${mkt}" >&2 2>&1 || \
                log "  WARNING: failed to register marketplace '${mkt}' — continuing"
        fi
    done

    # Pass 2: install each plugin
    echo "${CLAUDE_CODE_PLUGINS}" | jq -c '.[]' | while IFS= read -r plugin; do
        name=$(echo "${plugin}" | jq -r '.name')
        url=$(echo "${plugin}" | jq -r '.url')

        log "  Installing plugin '${name}' (${url})"
        claude plugin install "${url}" --scope project >&2 2>&1 || \
            log "  WARNING: failed to install plugin '${name}' — continuing"
    done
    log "--- Plugin installation complete ---"
fi

# ── Execute the Claude Code prompt ──────────────────────────────────────────
log "--- Executing prompt ---"
export WORK_DIR
python3 /workspace/execute_plugin.py

# ── Cleanup worktree ────────────────────────────────────────────────────────
log "--- Removing worktree ---"
cd /workspace
git -C "${REPO_DIR}" worktree remove "${WORK_DIR}" --force >&2 2>/dev/null || true

log "--- Execution complete ---"

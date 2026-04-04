#!/usr/bin/env bash
set -euo pipefail

# All progress logging to stderr — stdout is reserved for the JSON result
log() { echo "$@" >&2; }

log "=== Xianix Executor ==="
log "Tenant:              ${TENANT_ID}"
log "Claude Code Plugins: ${CLAUDE_CODE_PLUGINS}"

# Extract dynamic inputs from the XIANIX_INPUTS JSON blob.
# Scripts add new inputs to rules.json without any C# changes needed.
REPOSITORY_URL=$(echo "${XIANIX_INPUTS}" | jq -r '."repository-url" // empty')
PLATFORM=$(echo "${XIANIX_INPUTS}"       | jq -r '.platform // "github"')
GIT_REF=$(echo "${XIANIX_INPUTS}"        | jq -r '."pr-head-branch" // empty')

log "Repository:          ${REPOSITORY_URL}"
log "Platform:            ${PLATFORM}"
[ -n "${GIT_REF}" ] && log "Git ref:             ${GIT_REF}"

# Configure git credential helper based on platform
if [ "${PLATFORM}" = "github" ] && [ -n "${GITHUB_TOKEN:-}" ]; then
    git config --global url."https://${GITHUB_TOKEN}@github.com/".insteadOf "https://github.com/" >&2 2>&1
    # gh CLI reads GH_TOKEN for authentication
    export GH_TOKEN="${GITHUB_TOKEN}"
elif [ "${PLATFORM}" = "azuredevops" ] && [ -n "${AZURE_DEVOPS_TOKEN:-}" ]; then
    git config --global url."https://pat:${AZURE_DEVOPS_TOKEN}@dev.azure.com/".insteadOf "https://dev.azure.com/" >&2 2>&1
fi

# Volume is mounted at /workspace/repo — the repo directory is the persistence boundary.
# All executor scripts live at /workspace (image layer) and are never shadowed by the volume.
REPO_DIR="/workspace/repo"

# Smart clone: reuse existing repo if present, otherwise fresh clone
if [ -d "${REPO_DIR}/.git" ]; then
    log "--- Repository already cloned, fetching latest ---"
    cd "${REPO_DIR}"
    if ! git fetch --all --prune >&2 2>&1; then
        log "--- Fetch failed, repo may be corrupted. Re-cloning ---"
        cd /workspace && rm -rf "${REPO_DIR}"
        git clone "${REPOSITORY_URL}" "${REPO_DIR}" >&2 2>&1
        cd "${REPO_DIR}"
    else
        git reset --hard origin/HEAD >&2 2>&1
        git clean -fdx >&2 2>&1
    fi
else
    log "--- Cloning repository (first run for this tenant+repo) ---"
    git clone "${REPOSITORY_URL}" "${REPO_DIR}" >&2 2>&1
    cd "${REPO_DIR}"
fi

# Check out a specific branch/ref if provided (e.g. PR head)
if [ -n "${GIT_REF:-}" ]; then
    log "--- Checking out ref: ${GIT_REF} ---"
    git fetch origin "${GIT_REF}" >&2 2>&1
    git checkout FETCH_HEAD >&2 2>&1
fi

# Install Claude Code plugins declared in CLAUDE_CODE_PLUGINS.
# Each entry is a JSON object: { name, url, marketplace? }
#
#   url         — plugin reference in `plugin-name@marketplace-name` format passed to
#                 `claude plugin install` (e.g. `code-review@claude-plugins-official`)
#   marketplace — optional source for `claude plugin marketplace add` before installing.
#                 Accepts: GitHub `owner/repo`, a git URL, a local path, or a remote URL.
#                 Each unique marketplace is registered only once (deduplication).
#
# Failures on individual plugins are non-fatal — the prompt may still succeed with
# partial tooling.
if [ -n "${CLAUDE_CODE_PLUGINS:-}" ] && [ "${CLAUDE_CODE_PLUGINS}" != "[]" ]; then
    log "--- Installing Claude Code plugins ---"

    # Pass 1: register each unique marketplace once (skipped if already in user settings)
    echo "${CLAUDE_CODE_PLUGINS}" | jq -r '.[].marketplace // empty' | sort -u | while IFS= read -r mkt; do
        [ -z "${mkt}" ] && continue
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
        # `--scope project` adds the plugin to .claude/settings.json in the repo
        claude plugin install "${url}" --scope project >&2 2>&1 || \
            log "  WARNING: failed to install plugin '${name}' — continuing"
    done
    log "--- Plugin installation complete ---"
fi

# Execute the Claude Code prompt
# stdout IS the result — captured by the control plane via the Docker logs API
log "--- Executing prompt ---"
python3 /workspace/execute_plugin.py

log "--- Execution complete ---"

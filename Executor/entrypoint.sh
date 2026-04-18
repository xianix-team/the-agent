#!/usr/bin/env bash
set -euo pipefail

# Re-export dashed env vars as underscored aliases (bash can't use dashes in
# variable names). E.g. GITHUB-TOKEN → GITHUB_TOKEN. This lets the agent
# pass Key Vault names as-is while keeping the rest of the script standard.
# Use null-delimited env output to safely handle values with newlines or '='.
while IFS= read -r -d '' entry; do
  key="${entry%%=*}"
  value="${entry#*=}"
  [[ "$key" == *-* ]] && export "${key//-/_}=${value}"
done < <(env -0)

log() { echo "$@" >&2; }

log "=== Xianix Executor ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Claude Code Plugins: ${CLAUDE_CODE_PLUGINS}"

# ── Extract dynamic inputs ──────────────────────────────────────────────────
REPOSITORY_URL=$(echo "${XIANIX_INPUTS}" | jq -r '."repository-url" // empty')
PLATFORM=$(echo "${XIANIX_INPUTS}"       | jq -r '.platform // empty')
GIT_REF=$(echo "${XIANIX_INPUTS}"        | jq -r '."pr-head-branch" // empty')

if [ -n "${REPOSITORY_URL}" ] && [ -z "${PLATFORM}" ]; then
    PLATFORM="github"
fi

log "Repository:          ${REPOSITORY_URL:-<none>}"
log "Platform:            ${PLATFORM:-<none>}"
[ -n "${GIT_REF}" ] && log "Git ref:             ${GIT_REF}"

# ── Platform-specific credential & URL setup ─────────────────────────────────
GIT_CRED_FILE="/tmp/.git-credentials"

configure_credentials() {
    : > "${GIT_CRED_FILE}"

    case "${PLATFORM}" in
        github)
            if [ -n "${GITHUB_TOKEN:-}" ]; then
                printf 'https://x-access-token:%s@github.com\n' "${GITHUB_TOKEN}" >> "${GIT_CRED_FILE}"
                export GH_TOKEN="${GITHUB_TOKEN}"
            fi
            ;;
        azuredevops)
            if [ -z "${AZURE_DEVOPS_TOKEN:-}" ]; then
                log "FATAL: AZURE_DEVOPS_TOKEN is required when platform=azuredevops."
                exit 1
            fi
            REPOSITORY_URL=$(printf '%s' "${REPOSITORY_URL}" | sed -E 's|^https://[^@/]+@|https://|')
            printf 'https://pat:%s@dev.azure.com\n' "${AZURE_DEVOPS_TOKEN}" >> "${GIT_CRED_FILE}"

            if [[ "${REPOSITORY_URL}" =~ ^https://([^./]+)\.visualstudio\.com ]]; then
                printf 'https://pat:%s@%s.visualstudio.com\n' "${AZURE_DEVOPS_TOKEN}" "${BASH_REMATCH[1]}" >> "${GIT_CRED_FILE}"
            fi
            ;;
        *)
            log "WARNING: Unknown platform '${PLATFORM}' — no credentials configured."
            ;;
    esac

    if [ -s "${GIT_CRED_FILE}" ]; then
        chmod 600 "${GIT_CRED_FILE}"
        git config --global credential.helper "store --file=${GIT_CRED_FILE}"
    fi
}

# ── Prepare workspace ────────────────────────────────────────────────────────
REPO_DIR="/workspace/repo"
WORK_DIR="/workspace/exec-${EXECUTION_ID}"

prepare_repo_workspace() {
    configure_credentials

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

cleanup_repo_workspace() {
    git -C "${REPO_DIR}" worktree remove "${WORK_DIR}" --force >&2 2>/dev/null || true
}

cleanup_empty_workspace() {
    rm -rf "${WORK_DIR}"
}

if [ -n "${REPOSITORY_URL}" ]; then
    prepare_repo_workspace
else
    prepare_empty_workspace
fi

cd "${WORK_DIR}"
log "--- Workspace ready at ${WORK_DIR} ---"

# ── Install plugins ──────────────────────────────────────────────────────────
# Each entry is a JSON object: { "plugin-name", "marketplace"?, "envs"? }
#
#   plugin-name — plugin reference in `plugin-name@marketplace-name` format passed to
#                 `claude plugin install` (e.g. `pr-reviewer@xianix-plugins-official`)
#   marketplace — optional source for `claude plugin marketplace add` before installing.
#                 Each unique marketplace is registered only once (deduplication).
#
# Failures on individual plugins are non-fatal — the prompt may still succeed with
# partial tooling.
#
# Only consider objects with a non-empty string plugin-name. Skip JSON null array
# entries and malformed objects so we never run `claude plugin install null`.
_plugin_entry='select(
  (type == "object") and
  (has("plugin-name")) and
  (.["plugin-name"] | type == "string") and
  (.["plugin-name"] | length > 0)
)'

if [ -n "${CLAUDE_CODE_PLUGINS:-}" ] && [ "${CLAUDE_CODE_PLUGINS}" != "[]" ]; then
    log "--- Installing Claude Code plugins ---"

    echo "${CLAUDE_CODE_PLUGINS}" | jq -r ".[] | ${_plugin_entry} | .marketplace // empty" | sort -u | while IFS= read -r mkt; do
        [ -z "${mkt}" ] && continue
        [ "${mkt}" = "anthropics/claude-plugins-official" ] && continue
        if claude plugin marketplace list 2>/dev/null | grep -qF "${mkt}"; then
            log "  Marketplace '${mkt}' already registered — skipping"
        else
            log "  Registering marketplace '${mkt}'"
            claude plugin marketplace add "${mkt}" >&2 || \
                log "  WARNING: failed to register marketplace '${mkt}' — continuing"
        fi
    done

    echo "${CLAUDE_CODE_PLUGINS}" | jq -c ".[] | ${_plugin_entry}" | while IFS= read -r plugin; do
        name=$(echo "${plugin}" | jq -r '.["plugin-name"]' | cut -d@ -f1)
        url=$(echo "${plugin}"  | jq -r '.["plugin-name"]')

        log "  Installing plugin '${name}' (${url})"
        if claude plugin install "${url}" --scope project >&2; then
            installed_info=$(claude plugin list --json 2>/dev/null \
                | jq -r --arg id "${url}" '
                    .[]
                    | select(.id == $id)
                    | "\(.version // "unknown")\t\(.installPath // "")"
                  ' \
                | head -n1)
            if [ -n "${installed_info}" ]; then
                installed_version="${installed_info%%$'\t'*}"
                installed_path="${installed_info#*$'\t'}"
                log "  Installed '${name}' version ${installed_version}${installed_path:+ (path: ${installed_path})}"
            else
                log "  Installed '${name}' (version unavailable from 'claude plugin list')"
            fi
        else
            log "  WARNING: failed to install plugin '${name}' — continuing"
        fi
    done
    log "--- Plugin installation complete ---"
fi

# ── Execute the Claude Code prompt ──────────────────────────────────────────
log "--- Executing prompt ---"
export WORK_DIR
python3 /workspace/execute_plugin.py

# ── Cleanup workspace ────────────────────────────────────────────────────────
log "--- Cleaning up workspace ---"
cd /workspace
if [ -n "${REPOSITORY_URL}" ]; then
    cleanup_repo_workspace
else
    cleanup_empty_workspace
fi

log "--- Execution complete ---"

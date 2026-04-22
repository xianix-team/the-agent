#!/usr/bin/env bash
# Run phase of the executor pipeline.
#
# Assumes prepare_repo.sh has already created the workspace at WORK_DIR. In the
# default `prepare-and-execute` mode entrypoint.sh runs prepare_repo.sh first, so
# the worktree is guaranteed to exist when we get here.
#
# Steps:
#   1. Verify the workspace exists (fail-fast otherwise).
#   2. Install Claude Code plugins (best-effort; per-plugin failures are non-fatal).
#   3. Invoke execute_plugin.py to run the prompt.
#   4. Clean up the worktree (or empty workspace) so the next execution starts clean.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=_common.sh
source "${SCRIPT_DIR}/_common.sh"

log "=== Xianix Executor — run phase ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Claude Code Plugins: ${CLAUDE_CODE_PLUGINS:-[]}"

if [ ! -d "${WORK_DIR}" ]; then
    log "FATAL: Workspace '${WORK_DIR}' does not exist. " \
        "run_prompt.sh requires prepare_repo.sh to have run first " \
        "(use XIANIX_MODE=prepare-and-execute, the default)."
    exit 1
fi

cd "${WORK_DIR}"
log "--- Workspace ready at ${WORK_DIR} ---"

# ── Install plugins ──────────────────────────────────────────────────────────
# Each entry is a JSON object: { "plugin-name", "marketplace"? }
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
log "Working directory:   ${WORK_DIR}"
if [ -n "${PROMPT:-}" ]; then
    log "Prompt (${#PROMPT} chars) on ${REPOSITORY_URL:-<no repo>}${GIT_REF:+@${GIT_REF}}:"
    log "┌──────────────────────── PROMPT ────────────────────────"
    while IFS= read -r _line; do
        log "│ ${_line}"
    done <<< "${PROMPT}"
    log "└────────────────────────────────────────────────────────"
else
    log "WARNING: PROMPT env var is empty"
fi
export WORK_DIR
python3 /workspace/execute_plugin.py

# ── Cleanup workspace ────────────────────────────────────────────────────────
log "--- Cleaning up workspace ---"
cd /workspace
if [ -n "${REPOSITORY_URL}" ]; then
    git -C "${REPO_DIR}" worktree remove "${WORK_DIR}" --force >&2 2>/dev/null || true
else
    rm -rf "${WORK_DIR}"
fi

log "--- Execution complete ---"

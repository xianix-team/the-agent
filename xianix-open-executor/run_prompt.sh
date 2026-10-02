#!/usr/bin/env bash
# Run phase — OpenCode only.
#
# Assumes prepare_repo.sh already created WORK_DIR. Steps:
#   1. Verify workspace
#   2. Provision repo-declared runtimes (mise)
#   3. Inject cached repo context
#   4. Run execute_opencode.py
#   5. Cleanup worktree via EXIT trap
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=_common.sh
source "${SCRIPT_DIR}/_common.sh"

export XIANIX_MODEL="${XIANIX_MODEL:-}"

log "=== Xianix Open Executor — run phase ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Runtime:             opencode"
log "Model:               ${XIANIX_MODEL:-"(opencode default)"}"

if [ ! -d "${WORK_DIR}" ]; then
    log "FATAL: Workspace '${WORK_DIR}' does not exist. " \
        "run_prompt.sh requires prepare_repo.sh to have run first " \
        "(use XIANIX_MODE=prepare-and-execute, the default)."
    exit 1
fi

summarize_runtimes() {
    local ledger="${XIANIX_RUNTIME_FALLBACK_LOG:-}" kind detail
    [ -n "${ledger}" ] && [ -s "${ledger}" ] || return 0
    while IFS=$'\t' read -r kind detail; do
        case "${kind}" in
            downloaded) log "[runtimes] Fetched on demand: ${detail}" ;;
            cached)     log "[runtimes] Reused from cache: ${detail}" ;;
            failed)     log "[runtimes] WARNING: on-demand install FAILED: ${detail}" ;;
        esac
    done < <(awk -F'\t' '
        $3 == "downloaded" { d[$1 " " $2] = $4 }
        $3 == "cached"     { c[$1 " " $2] = 1 }
        $3 == "failed"     { f[$1] = 1 }
        END {
            for (k in d) dl = dl (dl ? ", " : "") k " (" d[k] ")"
            for (k in c) if (!(k in d)) ca = ca (ca ? ", " : "") k
            for (k in f) fa = fa (fa ? ", " : "") k "@latest"
            if (dl) print "downloaded\t" dl
            if (ca) print "cached\t" ca
            if (fa) print "failed\t" fa
        }' "${ledger}" 2>/dev/null)
    return 0
}

cleanup_workspace() {
    local exit_code=$?
    summarize_runtimes
    log "--- Cleaning up workspace ---"
    cd /workspace 2>/dev/null || true
    if [ -n "${REPOSITORY_URL:-}" ]; then
        git -C "${REPO_DIR}" worktree remove "${WORK_DIR}" --force >&2 2>/dev/null || true
    else
        rm -rf "${WORK_DIR}" 2>/dev/null || true
    fi
    log "--- Execution complete ---"
    return "${exit_code}"
}
trap cleanup_workspace EXIT

cd "${WORK_DIR}"
log "--- Workspace ready at ${WORK_DIR} ---"

if [ -n "${REPOSITORY_URL:-}" ] && [ -d "${REPO_DIR}" ]; then
    export OPENCODE_CONFIG_DIR="${REPO_DIR}/xianix-opencode-config"
    mkdir -p "${OPENCODE_CONFIG_DIR}" 2>/dev/null || true
    log "OpenCode config dir (persistent): ${OPENCODE_CONFIG_DIR}"
fi

# Repo-declared runtimes only.
_runtime_env_file="/tmp/xianix-runtime-env-${EXECUTION_ID}.sh"
"${SCRIPT_DIR}/provision_runtimes.sh" "${_runtime_env_file}" \
    || log "WARNING: runtime provisioning failed — continuing without extra runtimes."
if [ -s "${_runtime_env_file}" ]; then
    # shellcheck disable=SC1090
    source "${_runtime_env_file}"
    if [ -n "${XIANIX_PROVISIONED_RUNTIMES:-}" ]; then
        log "Runtime environment applied — on PATH: ${XIANIX_PROVISIONED_RUNTIMES}."
    else
        log "Runtime environment applied — mise sandbox only; no runtime beyond the image's own."
    fi
fi

if [ -n "${REPOSITORY_URL:-}" ]; then
    log "--- Preparing repo context (orientation + symbol map) ---"
    "${SCRIPT_DIR}/generate_context.sh" "${WORK_DIR}" "${REPO_DIR}/xianix-context" \
        || log "WARNING: context generation failed — continuing without it."
fi

log "--- Executing prompt (opencode) ---"
log "Working directory:   ${WORK_DIR}"
if [ -n "${PROMPT:-}" ]; then
    log "Prompt (${#PROMPT} chars) on ${REPOSITORY_URL:-<no repo>}:"
    log "┌──────────────────────── PROMPT ────────────────────────"
    while IFS= read -r _line; do
        log "│ ${_line}"
    done <<< "${PROMPT}"
    log "└────────────────────────────────────────────────────────"
else
    log "WARNING: PROMPT env var is empty"
fi
export WORK_DIR
python3 /workspace/execute_opencode.py

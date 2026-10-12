#!/usr/bin/env bash
# Run phase — OpenCode only.
#
# Assumes prepare_repo.sh already created WORK_DIR. Steps:
#   1. Verify workspace
#   2. Install plugins via claude plugin marketplace add/install (same as Executor/)
#   3. Stage install paths into XIANIX_PLUGINS_DIR for OpenCode .md conversion
#   4. Provision repo-declared runtimes (mise)
#   5. Inject cached repo context
#   6. Run execute_opencode.py
#   7. Cleanup worktree via EXIT trap
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=_common.sh
source "${SCRIPT_DIR}/_common.sh"

export XIANIX_MODEL="${XIANIX_MODEL:-}"

log "=== Executor.OpenCode — run phase ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Runtime:             opencode"
log "Model:               ${XIANIX_MODEL:-"(opencode default)"}"

# Per-run staging of Claude-installed plugin trees for OpenCode conversion.
export XIANIX_PLUGINS_DIR="${XIANIX_PLUGINS_DIR:-/tmp/xianix-plugins-${EXECUTION_ID}}"

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
    rm -rf "${XIANIX_PLUGINS_DIR}" 2>/dev/null || true
    log "--- Execution complete ---"
    return "${exit_code}"
}
trap cleanup_workspace EXIT

cd "${WORK_DIR}"
log "--- Workspace ready at ${WORK_DIR} ---"

# Claude config dir holds marketplace clones + plugin cache (same as Executor/).
# OpenCode config dir is separate for opencode session state.
if [ -n "${REPOSITORY_URL:-}" ] && [ -d "${REPO_DIR}" ]; then
    export CLAUDE_CONFIG_DIR="${REPO_DIR}/xianix-claude-config"
    export OPENCODE_CONFIG_DIR="${REPO_DIR}/xianix-opencode-config"
    mkdir -p "${CLAUDE_CONFIG_DIR}" "${OPENCODE_CONFIG_DIR}" 2>/dev/null || true
    log "Claude config dir (plugins): ${CLAUDE_CONFIG_DIR}"
    log "OpenCode config dir:         ${OPENCODE_CONFIG_DIR}"
fi

# ── Install plugins (same as Executor/: marketplace add + plugin install) ────
# Each entry is a JSON object: { "plugin-name", "marketplace"? }
#
#   plugin-name — plugin reference in `plugin-name@marketplace-name` format passed to
#                 `claude plugin install` (e.g. `pr-reviewer@xianix-plugins-official`)
#   marketplace — optional source for `claude plugin marketplace add` before installing.
#                 Each unique marketplace is registered only once (deduplication).
#
# Failures on individual plugins are non-fatal. After install, copy each
# installPath into XIANIX_PLUGINS_DIR so plugin_runtime can convert Claude .md
# → WORK_DIR/.opencode/. Prompts still run via OpenCode, not Claude Code.
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
    log "--- Installing Claude Code plugins (for OpenCode conversion) ---"
    mkdir -p "${XIANIX_PLUGINS_DIR}"

    # Force a fresh clone of every marketplace this run needs (same as Executor/).
    # Marketplace clones live on the persistent volume, but neither
    # `marketplace add` nor `marketplace update` reliably refreshes an already
    # registered clone. Remove non-official registrations, then re-add.
    { claude plugin marketplace list --json 2>/dev/null \
        | jq -r '.[] | select(.name != "claude-plugins-official") | .name' 2>/dev/null \
        | while IFS= read -r _name; do
            [ -z "${_name}" ] && continue
            log "  Removing stale marketplace clone '${_name}'"
            claude plugin marketplace remove "${_name}" >&2 2>/dev/null || true
        done ; } || true

    echo "${CLAUDE_CODE_PLUGINS}" | jq -r ".[] | ${_plugin_entry} | .marketplace // empty" | sort -u | while IFS= read -r mkt; do
        [ -z "${mkt}" ] && continue
        [ "${mkt}" = "anthropics/claude-plugins-official" ] && continue
        log "  Registering marketplace '${mkt}' (fresh clone)"
        claude plugin marketplace add "${mkt}" >&2 || \
            log "  WARNING: failed to register marketplace '${mkt}' — continuing"
    done

    # Report version+path from `claude plugin list` (on-disk cache).
    _installed_info() {
        claude plugin list --json 2>/dev/null \
            | jq -r --arg id "$1" '
                first(.[] | select(.id == $id) | "\(.version // "unknown")\t\(.installPath // "")")
              ' 2>/dev/null
    }

    # Wipe per-plugin cache so install re-copies from the fresh marketplace clone.
    _cache_base="${CLAUDE_CONFIG_DIR:-${HOME}/.claude}/plugins/cache"

    echo "${CLAUDE_CODE_PLUGINS}" | jq -c ".[] | ${_plugin_entry}" | while IFS= read -r plugin; do
        url=$(echo "${plugin}" | jq -r '.["plugin-name"]')
        name="${url%@*}"          # plugin short name (before @)
        mkt_name="${url##*@}"     # marketplace name (after @)
        [ "${mkt_name}" = "${url}" ] && mkt_name=""   # no @ → marketplace unknown

        log "  Installing plugin '${name}' (${url})"

        claude plugin uninstall "${url}" --scope project >&2 2>/dev/null || true
        [ -n "${mkt_name}" ] && rm -rf "${_cache_base:?}/${mkt_name:?}/${name:?}" 2>/dev/null || true

        if ! claude plugin install "${url}" --scope project >&2; then
            log "  WARNING: failed to install plugin '${name}' — continuing"
            continue
        fi

        installed_info="$(_installed_info "${url}")" || true
        installed_version="unknown"
        installed_path=""
        if [ -n "${installed_info}" ]; then
            installed_version="${installed_info%%$'\t'*}"
            installed_path="${installed_info#*$'\t'}"
            log "  Installed '${name}' version ${installed_version}${installed_path:+ (path: ${installed_path})}"
        else
            log "  Installed '${name}' (version unavailable from 'claude plugin list')"
        fi

        # Stage under short name for plugin_runtime.discover_plugin_roots.
        if [ -n "${installed_path}" ] && [ -d "${installed_path}" ]; then
            rm -rf "${XIANIX_PLUGINS_DIR:?}/${name}"
            if cp -a "${installed_path}" "${XIANIX_PLUGINS_DIR}/${name}"; then
                log "  Staged '${name}' → ${XIANIX_PLUGINS_DIR}/${name}"
            else
                log "  WARNING: failed to stage plugin '${name}' for OpenCode conversion"
            fi
        else
            log "  WARNING: no installPath for '${name}' — OpenCode conversion may miss it"
        fi
    done
    log "--- Plugin installation complete ---"

    # Hide .claude/settings.json from plugin git diffs (same as Executor/).
    if [ -n "${REPOSITORY_URL:-}" ]; then
        _git_dir="$(git -C "${WORK_DIR}" rev-parse --absolute-git-dir 2>/dev/null || echo "")"
        if [ -n "${_git_dir}" ]; then
            mkdir -p "${_git_dir}/info" 2>/dev/null || true
            _exclude_file="${_git_dir}/info/exclude"
            grep -qxF ".claude/" "${_exclude_file}" 2>/dev/null \
                || printf '%s\n' ".claude/" >> "${_exclude_file}" 2>/dev/null || true
        fi
    fi
else
    log "No CLAUDE-CODE-PLUGINS requested — skipping marketplace install"
    mkdir -p "${XIANIX_PLUGINS_DIR}"
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
    log "Prompt (${#PROMPT} chars) on $(redact_repo_url "${REPOSITORY_URL:-<no repo>}"):"
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

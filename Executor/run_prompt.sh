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

# ── Crash-safe workspace cleanup ─────────────────────────────────────────────
# execute_plugin.py exits 1 on an error envelope (e.g. a budget abort), and this
# script runs under `set -e`, so a straight-line cleanup at the end would be
# skipped on any failure — leaking worktree admin metadata onto the shared volume
# (which then accumulates until a future prune reaps it). Running cleanup from an
# EXIT trap guarantees the worktree is removed on both success and failure, while
# preserving the original exit code so the control plane still sees the real
# outcome.
cleanup_workspace() {
    local exit_code=$?
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

# ── Persist Claude Code config/session store across runs ─────────────────────
# By default Claude Code keeps settings, credentials, and session history under ~/.claude,
# which is wiped with every ephemeral container. Relocating it onto the tenant volume lets
# back-to-back runs (e.g. PR re-reviews on `synchronize`) reuse prior session history for
# session resume, and keeps the prompt prefix stable across runs so Anthropic prompt caching
# can hit. Only done when a repo volume is mounted (no volume = nothing to persist to).
if [ -n "${REPOSITORY_URL:-}" ] && [ -d "${REPO_DIR}" ]; then
    export CLAUDE_CONFIG_DIR="${REPO_DIR}/xianix-claude-config"
    mkdir -p "${CLAUDE_CONFIG_DIR}" 2>/dev/null || true
    log "Claude config dir (persistent): ${CLAUDE_CONFIG_DIR}"
fi

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

    # ── Force a fresh clone of every marketplace this run needs ──────────────
    #
    # Marketplace clones live on the persistent volume, but neither
    # `marketplace add` nor `marketplace update` reliably refreshes an already
    # registered clone: `add` is a no-op ("already on disk") and `update` cannot
    # cleanly recover a clone whose branch was force-pushed. A force-pushed (or
    # otherwise diverged) clone ends up with its top-level marketplace.json
    # updated while an individual plugin's own plugin.json — the file that
    # `plugin install` actually reads the version from — stays stale, so the run
    # silently installs an old plugin version. The only reliable recovery is to
    # remove the registration and re-add it, which re-clones from source. So drop
    # every non-official registration first, then add the ones we need fresh.
    # `marketplace remove` also uninstalls that marketplace's plugins, giving the
    # install loop below a clean slate.
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

    # Report the version+path currently installed for a plugin id (tab-separated),
    # read straight from `claude plugin list` so it reflects the on-disk cache.
    _installed_info() {
        claude plugin list --json 2>/dev/null \
            | jq -r --arg id "$1" '
                first(.[] | select(.id == $id) | "\(.version // "unknown")\t\(.installPath // "")")
              ' 2>/dev/null
    }

    # Per-plugin cache lives under the (persistent) config dir. Wiping a plugin's
    # cache dir before install forces `plugin install` to re-copy from the fresh
    # marketplace clone — covering the case where a plugin's contents changed but
    # its version string did not (which would otherwise reuse the stale cache).
    _cache_base="${CLAUDE_CONFIG_DIR:-${HOME}/.claude}/plugins/cache"

    echo "${CLAUDE_CODE_PLUGINS}" | jq -c ".[] | ${_plugin_entry}" | while IFS= read -r plugin; do
        url=$(echo "${plugin}"  | jq -r '.["plugin-name"]')
        name="${url%@*}"          # plugin short name (before @)
        mkt_name="${url##*@}"     # marketplace name (after @)
        [ "${mkt_name}" = "${url}" ] && mkt_name=""   # no @ → marketplace unknown

        log "  Installing plugin '${name}' (${url})"

        # Clear any prior install record + cached copy so install always resolves
        # and copies the version from the freshly cloned marketplace.
        claude plugin uninstall "${url}" --scope project >&2 2>/dev/null || true
        [ -n "${mkt_name}" ] && rm -rf "${_cache_base}/${mkt_name}/${name}" 2>/dev/null || true

        if ! claude plugin install "${url}" --scope project >&2; then
            log "  WARNING: failed to install plugin '${name}' — continuing"
            continue
        fi

        installed_info="$(_installed_info "${url}")" || true
        if [ -n "${installed_info}" ]; then
            installed_version="${installed_info%%$'\t'*}"
            installed_path="${installed_info#*$'\t'}"
            log "  Installed '${name}' version ${installed_version}${installed_path:+ (path: ${installed_path})}"
        else
            log "  Installed '${name}' (version unavailable from 'claude plugin list')"
        fi
    done
    log "--- Plugin installation complete ---"

    # `claude plugin install --scope project` writes .claude/settings.json into the
    # worktree. Left tracked, it pollutes a plugin's own `git status` / `git diff`
    # (e.g. PR-review diffs would show a spurious .claude/ change). Add it to the
    # worktree-local git exclude — mirroring how generate_context.sh hides its
    # injected CLAUDE.md / .xianix/ — so plugin diffs stay clean.
    if [ -n "${REPOSITORY_URL:-}" ]; then
        _git_dir="$(git -C "${WORK_DIR}" rev-parse --absolute-git-dir 2>/dev/null || echo "")"
        if [ -n "${_git_dir}" ]; then
            mkdir -p "${_git_dir}/info" 2>/dev/null || true
            _exclude_file="${_git_dir}/info/exclude"
            grep -qxF ".claude/" "${_exclude_file}" 2>/dev/null \
                || printf '%s\n' ".claude/" >> "${_exclude_file}" 2>/dev/null || true
        fi
    fi
fi

# ── Prepare cached repo context (CLAUDE.md + symbol map) ─────────────────────
# Deterministic, token-free orientation cached on the volume and injected into the worktree
# so the agent doesn't re-explore the codebase from scratch. Best-effort: never fails the run.
if [ -n "${REPOSITORY_URL:-}" ]; then
    log "--- Preparing repo context (CLAUDE.md + symbol map) ---"
    "${SCRIPT_DIR}/generate_context.sh" "${WORK_DIR}" "${REPO_DIR}/xianix-context" \
        || log "WARNING: context generation failed — continuing without it."
fi

# ── Execute the Claude Code prompt ──────────────────────────────────────────
log "--- Executing prompt ---"
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
# Cleanup (and the final "Execution complete" log) runs from the EXIT trap
# installed above, so it fires whether this succeeds or exits non-zero.
python3 /workspace/execute_plugin.py

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
    # Kill the per-run Headroom proxy (if started) BEFORE we touch the worktree, so it
    # can't hold any file handles. Best-effort: never fail cleanup on this.
    if [ -n "${HEADROOM_PID:-}" ]; then
        kill "${HEADROOM_PID}" 2>/dev/null || true
        wait "${HEADROOM_PID}" 2>/dev/null || true
    fi
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

    # Registered marketplaces are cached on the persistent volume and are not
    # refreshed by `marketplace add` when already present, so their clones can
    # pin plugins to a stale version. Refresh all marketplaces from source so
    # `plugin install`/`plugin update` below resolve the latest versions.
    log "  Updating registered marketplaces to latest"
    claude plugin marketplace update >&2 || \
        log "  WARNING: marketplace update failed — plugin versions may be stale"

    echo "${CLAUDE_CODE_PLUGINS}" | jq -c ".[] | ${_plugin_entry}" | while IFS= read -r plugin; do
        name=$(echo "${plugin}" | jq -r '.["plugin-name"]' | cut -d@ -f1)
        url=$(echo "${plugin}"  | jq -r '.["plugin-name"]')

        log "  Installing plugin '${name}' (${url})"
        if claude plugin install "${url}" --scope project >&2; then
            # A prior run may have cached an older version; `plugin install` is a
            # no-op when already installed, so bump to the latest now available in
            # the refreshed marketplace. Best-effort — a failure just leaves the
            # currently installed version in place.
            claude plugin update "${url}" --scope project >&2 || true
            installed_info=$(claude plugin list --json 2>/dev/null \
                | jq -r --arg id "${url}" '
                    first(.[] | select(.id == $id) | "\(.version // "unknown")\t\(.installPath // "")")
                  ' 2>/dev/null) || true
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

# ── Optional: Headroom compression proxy (Option B, per-container, fail-open) ──────────
# When XIANIX-COMPRESSION=1 the executor starts a local Headroom proxy and points the
# Claude Code CLI at it via ANTHROPIC_BASE_URL. If the proxy fails to come up healthy,
# we log a warning and continue with ANTHROPIC_BASE_URL unset — compression is an
# optimisation and must NEVER break a plugin run. `_common.sh` re-exports dashed env
# vars as underscored aliases, but we also honour the raw XIANIX-COMPRESSION form for
# safety in case this script is invoked stand-alone.
_compression_flag="${XIANIX_COMPRESSION:-$(printenv 'XIANIX-COMPRESSION' 2>/dev/null || true)}"
case "${_compression_flag:-}" in
    1|true|TRUE|True|yes|YES)
        log "--- Starting Headroom compression proxy (opt-in via XIANIX-COMPRESSION) ---"
        export HEADROOM_TELEMETRY=off
        export HEADROOM_SAVINGS_PATH="/tmp/headroom-${EXECUTION_ID}.json"
        # Route all proxy output to stderr so it doesn't pollute the executor's stdout
        # JSON envelope. Fail-open: if `headroom` isn't installed we log and skip.
        if command -v headroom >/dev/null 2>&1; then
            headroom proxy --host 127.0.0.1 --port 8787 --no-telemetry \
                >&2 2>&1 &
            HEADROOM_PID=$!
            # Wait up to ~6s for /health before routing traffic through the proxy.
            _healthy=0
            for _ in $(seq 1 30); do
                if curl -sf http://127.0.0.1:8787/health >/dev/null 2>&1; then
                    _healthy=1
                    break
                fi
                sleep 0.2
            done
            if [ "${_healthy}" = "1" ]; then
                export ANTHROPIC_BASE_URL="http://127.0.0.1:8787"
                log "Headroom proxy healthy (pid ${HEADROOM_PID}); ANTHROPIC_BASE_URL=${ANTHROPIC_BASE_URL}"
            else
                log "WARNING: Headroom proxy did not become healthy in time — continuing without compression."
                kill "${HEADROOM_PID}" 2>/dev/null || true
                unset HEADROOM_PID
            fi
        else
            log "WARNING: XIANIX-COMPRESSION set but 'headroom' CLI not found in image — continuing without compression."
        fi
        ;;
esac

# Cleanup (and the final "Execution complete" log) runs from the EXIT trap
# installed above, so it fires whether this succeeds or exits non-zero.
python3 /workspace/execute_plugin.py

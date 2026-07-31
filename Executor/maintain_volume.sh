#!/usr/bin/env bash
# Best-effort maintenance of the persistent tenant volume.
#
# Nothing on the volume is ever garbage-collected by the rest of the pipeline, so
# without this it grows unbounded: git objects pile up in the bare clone, the
# plugin cache keeps every version it ever installed (…/1.4.0, …/1.7.0, …), and
# the session-id pointers accumulate one file per conversation forever.
#
# This runs during the prepare phase (before plugin install) and is strictly an
# optimisation: every step is guarded and the script always exits 0 so a
# maintenance hiccup can never fail an execution. It is also written to be safe
# under concurrency — it never removes the newest plugin version dir (which a
# peer container may have just installed) and never touches the bare repo's refs.
#
# Usage: maintain_volume.sh <repo_dir>
set -uo pipefail

log() { echo "[maintain] $*" >&2; }

REPO_DIR="${1:-/workspace/repo}"

# Retention knobs (host-overridable). Days.
SESSION_RETENTION_DAYS="${XIANIX_SESSION_RETENTION_DAYS:-30}"
PLUGIN_CACHE_MAX_AGE_DAYS="${XIANIX_PLUGIN_CACHE_MAX_AGE_DAYS:-7}"
RUNTIME_MAX_AGE_DAYS="${XIANIX_RUNTIME_MAX_AGE_DAYS:-30}"
NUGET_CACHE_MAX_MB="${XIANIX_NUGET_CACHE_MAX_MB:-4096}"

if [ ! -d "${REPO_DIR}" ]; then
    log "repo dir '${REPO_DIR}' missing — nothing to maintain."
    exit 0
fi

# ── 1. Git object housekeeping on the bare clone ─────────────────────────────
# `--auto` only actually packs when git's own heuristics say it's worthwhile, so
# this is cheap on most runs and prevents loose-object blowup over time.
if git -C "${REPO_DIR}" gc --auto >&2 2>/dev/null; then
    :
else
    log "git gc --auto failed — continuing."
fi

# ── 2. Session-id pointer retention ──────────────────────────────────────────
# One small file per conversation (repo#pr). Drop the ones untouched for a while;
# a resume that finds no pointer simply falls back to a fresh run.
sess_dir="${REPO_DIR}/xianix-sessions"
if [ -d "${sess_dir}" ]; then
    find "${sess_dir}" -type f -mtime +"${SESSION_RETENTION_DAYS}" -delete 2>/dev/null \
        && log "Pruned session pointers older than ${SESSION_RETENTION_DAYS}d." || true
fi

# ── 3. Plugin cache: drop stale superseded versions ──────────────────────────
# Layout: xianix-claude-config/plugins/cache/<marketplace>/<plugin>/<version>/.
# For each plugin, always keep the newest version dir (a concurrent run may have
# just installed it), and remove OTHER version dirs only once they've been
# untouched for the age threshold — so an in-flight install is never reaped.
cache_root="${REPO_DIR}/xianix-claude-config/plugins/cache"
if [ -d "${cache_root}" ]; then
    while IFS= read -r plugin_dir; do
        [ -d "${plugin_dir}" ] || continue
        newest="$(ls -1dt "${plugin_dir}"/*/ 2>/dev/null | head -n1)"
        for vdir in "${plugin_dir}"/*/; do
            [ -d "${vdir}" ] || continue
            [ "${vdir}" = "${newest}" ] && continue
            # `find -maxdepth 0 -mtime +N` deletes the dir only when old enough.
            if find "${vdir}" -maxdepth 0 -type d -mtime +"${PLUGIN_CACHE_MAX_AGE_DAYS}" 2>/dev/null | grep -q .; then
                rm -rf "${vdir}" 2>/dev/null \
                    && log "Removed stale plugin cache dir: ${vdir}" || true
            fi
        done
    done < <(find "${cache_root}" -mindepth 2 -maxdepth 2 -type d 2>/dev/null)
fi

# ── 4. Runtime cache: prune runtimes nobody has used in a while ──────────────
# provision_runtimes.sh touches .meta/<name>-<version>.last-used on every run
# that requests a runtime; anything whose marker goes stale for the retention
# window is reaped. Runs against both possible cache roots: the shared runtime
# volume and the per-repo fallback. The provisioning flock is taken non-blocking
# so pruning never races a concurrent install — if an install is in flight we
# simply skip maintenance this round.
prune_runtime_root() {
    local root="$1"
    [ -d "${root}/.meta" ] || return 0

    (
        flock -n 9 || { log "runtime cache at '${root}' busy (install in flight) — skipping prune."; exit 0; }

        # node-<version> dirs are self-contained: reap dir + marker together.
        local marker version dir
        while IFS= read -r marker; do
            [ -f "${marker}" ] || continue
            version="$(basename "${marker}")"
            version="${version#node-}"; version="${version%.last-used}"
            dir="${root}/node-${version}"
            rm -rf "${dir}" 2>/dev/null
            rm -f "${marker}" 2>/dev/null
            log "Removed stale runtime: node ${version} (unused for ${RUNTIME_MAX_AGE_DAYS}d)."
        done < <(find "${root}/.meta" -maxdepth 1 -type f -name 'node-*.last-used' \
                     -mtime +"${RUNTIME_MAX_AGE_DAYS}" 2>/dev/null)

        # dotnet SDK versions share one root (the muxer hosts them side by side),
        # so a single version can't be carved out safely. Reap the whole root only
        # once EVERY dotnet last-used marker is stale.
        if [ -d "${root}/dotnet" ]; then
            local total fresh
            total=$(find "${root}/.meta" -maxdepth 1 -type f -name 'dotnet-*.last-used' 2>/dev/null | wc -l)
            fresh=$(find "${root}/.meta" -maxdepth 1 -type f -name 'dotnet-*.last-used' \
                        -mtime -"${RUNTIME_MAX_AGE_DAYS}" 2>/dev/null | wc -l)
            if [ "${total}" -gt 0 ] && [ "${fresh}" -eq 0 ]; then
                rm -rf "${root}/dotnet" 2>/dev/null
                find "${root}/.meta" -maxdepth 1 -type f -name 'dotnet-*.last-used' -delete 2>/dev/null
                log "Removed stale runtime: dotnet (all versions unused for ${RUNTIME_MAX_AGE_DAYS}d)."
            fi
        fi

        # NuGet package cache: pure cache, so when it outgrows the cap wipe it
        # wholesale (the next restore rebuilds what's actually needed). A restore
        # running concurrently in another container may fail once and self-heal
        # on its next run — acceptable for a bounded-disk guarantee.
        local nuget_dir="${root}/cache/nuget"
        if [ -d "${nuget_dir}" ]; then
            local size_mb
            size_mb=$(du -sm "${nuget_dir}" 2>/dev/null | cut -f1)
            if [ "${size_mb:-0}" -gt "${NUGET_CACHE_MAX_MB}" ]; then
                rm -rf "${nuget_dir}" 2>/dev/null
                log "Wiped NuGet cache at '${nuget_dir}' (${size_mb}MB > ${NUGET_CACHE_MAX_MB}MB cap)."
            fi
        fi
    ) 9>"${root}/.provision.lock" 2>/dev/null || true
}

prune_runtime_root "/workspace/runtimes"
prune_runtime_root "${REPO_DIR}/xianix-runtimes"

exit 0

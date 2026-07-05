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

exit 0

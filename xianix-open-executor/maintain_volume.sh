#!/usr/bin/env bash
# Best-effort maintenance of the persistent tenant volume (OpenCode executor).
#
# Prunes git objects, session pointers, and unused mise runtimes. Always exits 0.
#
# Usage: maintain_volume.sh <repo_dir>
set -uo pipefail

log() { echo "[maintain] $*" >&2; }

REPO_DIR="${1:-/workspace/repo}"

SESSION_RETENTION_DAYS="${XIANIX_SESSION_RETENTION_DAYS:-30}"
RUNTIME_MAX_AGE_DAYS="${XIANIX_RUNTIME_MAX_AGE_DAYS:-30}"
NUGET_CACHE_MAX_MB="${XIANIX_NUGET_CACHE_MAX_MB:-4096}"

if [ ! -d "${REPO_DIR}" ]; then
    log "repo dir '${REPO_DIR}' missing — nothing to maintain."
    exit 0
fi

if git -C "${REPO_DIR}" gc --auto >&2 2>/dev/null; then
    :
else
    log "git gc --auto failed — continuing."
fi

sess_dir="${REPO_DIR}/xianix-sessions"
if [ -d "${sess_dir}" ]; then
    find "${sess_dir}" -type f -mtime +"${SESSION_RETENTION_DAYS}" -delete 2>/dev/null \
        && log "Pruned session pointers older than ${SESSION_RETENTION_DAYS}d." || true
fi

# OpenCode config dir is small; opportunistic prune of very old session DB WAL leftovers
# is left to OpenCode itself.
# provision_runtimes.sh touches .meta/<tool>@<version>.last-used for every
# runtime a run resolves; anything whose marker goes stale for the retention
# window is handed to `mise uninstall` rather than rm -rf'd, so shared roots like
# dotnet's (where every SDK version lives side by side under one DOTNET_ROOT) are
# unwound correctly. Runs against both possible cache roots: the shared runtime
# volume and the per-repo fallback. The provisioning flock is taken non-blocking
# so pruning never races a concurrent install — if an install is in flight we
# simply skip maintenance this round.
prune_runtime_root() {
    local root="$1"
    local data_dir="${root}/mise"
    [ -d "${data_dir}/installs" ] || return 0
    command -v mise >/dev/null 2>&1 || return 0
    # Create the lock file up front and bail if we can't: that way the subshell's
    # fd-9 redirect below can't fail, and its stderr (i.e. every log line this
    # function emits) doesn't have to be discarded to keep that failure quiet.
    touch "${root}/.provision.lock" 2>/dev/null || return 0

    (
        flock -n 9 || { log "runtime cache at '${root}' busy (install in flight) — skipping prune."; exit 0; }

        export MISE_DATA_DIR="${data_dir}"
        export MISE_CACHE_DIR="${root}/mise-cache"
        export MISE_STATE_DIR="${root}/mise-state"
        export MISE_SAFE=1
        export MISE_YES=1
        mkdir -p "${root}/.meta" 2>/dev/null || true

        # Adopt installs that have no marker — either they predate marker tracking
        # or a prune removed the marker but not the install. Giving them a marker
        # now starts their retention window instead of letting them live forever.
        # This asks mise rather than walking installs/: that directory is mostly
        # alias symlinks (node/22, dotnet/latest, …) pointing at one concrete
        # version, and a marker per alias would let a stale `node@22` uninstall a
        # `node@22.11.0` that is still in active use.
        local tool version marker spec
        while IFS=$'\t' read -r tool version; do
            [ -n "${tool}" ] && [ -n "${version}" ] || continue
            marker="${root}/.meta/${tool}@${version}.last-used"
            [ -e "${marker}" ] || touch "${marker}" 2>/dev/null || true
        done < <(mise --no-config ls --installed --json 2>/dev/null \
            | jq -r 'to_entries[] | .key as $t | .value[]
                     | select(.installed == true) | "\($t)\t\(.version)"' 2>/dev/null)

        while IFS= read -r marker; do
            [ -f "${marker}" ] || continue
            spec="$(basename "${marker}")"; spec="${spec%.last-used}"
            case "${spec}" in *@*) ;; *) continue ;; esac
            if mise --no-config uninstall "${spec}" >/dev/null 2>&1; then
                log "Removed stale runtime: ${spec} (unused for ${RUNTIME_MAX_AGE_DAYS}d)."
            fi
            rm -f "${marker}" 2>/dev/null || true
        done < <(find "${root}/.meta" -maxdepth 1 -type f -name '*.last-used' \
                     -mtime +"${RUNTIME_MAX_AGE_DAYS}" 2>/dev/null)

        # Uninstalling a .NET SDK only removes that version's sdk/ directory from
        # the shared dotnet-root; the shared runtime under dotnet-root/shared
        # (a couple of hundred MB) survives. Once no dotnet version is left at
        # all, the whole root is dead weight. Requires a well-formed answer from
        # mise so a failed query can never be read as "no dotnet installed".
        local installed_json
        installed_json="$(mise --no-config ls --installed --json 2>/dev/null)"
        if [ -d "${data_dir}/dotnet-root" ] \
            && jq -e 'type == "object"'  >/dev/null 2>&1 <<<"${installed_json}" \
            && ! jq -e 'has("dotnet")'   >/dev/null 2>&1 <<<"${installed_json}"; then
            rm -rf "${data_dir}/dotnet-root" 2>/dev/null \
                && log "Removed the orphaned shared .NET root (no SDK version left)."
        fi

        # mise's download/metadata cache is pure cache and prunes itself by age.
        mise --no-config cache prune >/dev/null 2>&1 || true

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
    ) 9>"${root}/.provision.lock" || true
}

prune_runtime_root "/workspace/runtimes"
prune_runtime_root "${REPO_DIR}/xianix-runtimes"

exit 0

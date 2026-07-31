#!/usr/bin/env bash
# Provision plugin-declared runtimes onto the persistent runtime volume.
#
# Plugins that need to build/run code (e.g. a unit-test writer verifying with
# `dotnet test`) declare their runtime requirements in a manifest file at the
# plugin root:
#
#   xianix-runtimes.json:
#     { "runtimes": [ { "name": "dotnet", "version": "9.0" } ] }
#
# After plugin install, run_prompt.sh calls this script. It collects the
# manifests of every installed plugin, installs any missing runtimes (user-space
# only — the container runs as non-root with no-new-privileges, so no apt) into
# the runtime cache, and writes `export` lines to the env file given as $1.
# run_prompt.sh sources that file so the runtimes are on PATH for Claude Code's
# Bash tool.
#
# Cache location (RUNTIMES_ROOT):
#   /workspace/runtimes            — the shared per-tenant volume mounted by the
#                                    control plane; one SDK download serves every
#                                    repo and plugin of the tenant.
#   ${REPO_DIR}/xianix-runtimes    — fallback when no runtime volume is mounted
#                                    (local `docker run`, older control planes).
#
# Cache contract:
#   * dotnet installs share ONE root (${RUNTIMES_ROOT}/dotnet) — the muxer hosts
#     multiple SDK versions side by side; a `.xianix-ok-<version>` marker records
#     each completed channel install.
#   * node versions get isolated dirs (${RUNTIMES_ROOT}/node-<version>) built in
#     a temp dir and atomically `mv`ed into place.
#   * All installs run under one flock (${RUNTIMES_ROOT}/.provision.lock) so
#     concurrent containers — possibly serving different repos of the tenant —
#     never clobber each other. Cache hits don't take the lock.
#   * Every requested runtime touches ${RUNTIMES_ROOT}/.meta/<name>-<version>.last-used
#     so maintain_volume.sh can prune runtimes nobody has used in a while.
#
# Security: only allow-listed runtime names are honoured, versions must match a
# strict pattern, and download URLs are built from constants — a manifest can
# never make this script fetch an arbitrary URL or run arbitrary commands.
#
# Best-effort like plugin install: a failed runtime install logs a warning and
# is skipped; the script always exits 0 so provisioning can never fail a run.
set -uo pipefail

log() { echo "[runtimes] $*" >&2; }

ENV_FILE="${1:-}"
if [ -z "${ENV_FILE}" ]; then
    log "FATAL: usage: provision_runtimes.sh <env_out_file>"
    exit 0
fi
: > "${ENV_FILE}" 2>/dev/null || { log "WARNING: cannot write env file '${ENV_FILE}' — skipping provisioning."; exit 0; }

MANIFEST_NAME="xianix-runtimes.json"
DOTNET_INSTALL_SCRIPT="/workspace/dotnet-install.sh"
NODE_DIST_BASE="https://nodejs.org/dist"

# ── Resolve the runtime cache root ────────────────────────────────────────────
# /workspace/runtimes always exists in the image (it's the mount point), so a
# plain -d test can't tell "volume mounted" from "bare container dir" — and the
# latter is ephemeral, which would silently discard every install. Require an
# actual mountpoint before trusting it.
if mountpoint -q /workspace/runtimes 2>/dev/null && [ -w "/workspace/runtimes" ]; then
    RUNTIMES_ROOT="/workspace/runtimes"
elif [ -n "${REPO_DIR:-}" ] && [ -d "${REPO_DIR}" ] && [ -w "${REPO_DIR}" ]; then
    RUNTIMES_ROOT="${REPO_DIR}/xianix-runtimes"
    log "No shared runtime volume mounted — falling back to per-repo cache at ${RUNTIMES_ROOT}."
else
    RUNTIMES_ROOT="/tmp/xianix-runtimes"
    log "No persistent volume available — runtimes will not survive this container (${RUNTIMES_ROOT})."
fi
mkdir -p "${RUNTIMES_ROOT}/.meta" 2>/dev/null || { log "WARNING: cannot create ${RUNTIMES_ROOT} — skipping provisioning."; exit 0; }
LOCK_FILE="${RUNTIMES_ROOT}/.provision.lock"

# ── Collect manifests from installed plugins ──────────────────────────────────
# One "name<TAB>version" line per requested runtime, deduplicated. Malformed
# manifests are logged and skipped, never fatal.
collect_requests() {
    local install_paths manifest
    install_paths=$(claude plugin list --json 2>/dev/null \
        | jq -r '.[].installPath // empty' 2>/dev/null) || true

    while IFS= read -r plugin_path; do
        [ -n "${plugin_path}" ] && [ -d "${plugin_path}" ] || continue
        manifest="${plugin_path}/${MANIFEST_NAME}"
        [ -f "${manifest}" ] || continue

        if ! jq -e '.runtimes | type == "array"' "${manifest}" >/dev/null 2>&1; then
            log "WARNING: ${manifest} is malformed (expected { \"runtimes\": [...] }) — skipping."
            continue
        fi

        jq -r '
            .runtimes[]
            | select(type == "object")
            | select((.name | type == "string") and (.name | length > 0))
            | select((.version | type == "string") and (.version | length > 0))
            | "\(.name)\t\(.version)"
        ' "${manifest}" 2>/dev/null
    done <<< "${install_paths}" | sort -u
}

# ── Validation ────────────────────────────────────────────────────────────────
# Versions: digits/letters/dots/dashes only (9.0, 9.0.203, 22.11.0, LTS, STS).
# No slashes or leading dots — version strings become path components and URL
# segments, so this also blocks traversal.
valid_version() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9.-]{0,31}$ ]]; }

# ── Env emission ──────────────────────────────────────────────────────────────
emit_env() { printf '%s\n' "$1" >> "${ENV_FILE}"; }

PROVISIONED=()   # human-readable "name version" entries for the summary export

mark_used() { touch "${RUNTIMES_ROOT}/.meta/$1-$2.last-used" 2>/dev/null || true; }

# ── Provider: dotnet ──────────────────────────────────────────────────────────
# All versions share one install root; the dotnet muxer resolves the SDK per
# project (global.json). `version` is passed as --channel for x.y / LTS / STS
# and as --version for a fully-pinned x.y.z.
DOTNET_ENV_EMITTED=0

provision_dotnet() {
    local version="$1"
    local root="${RUNTIMES_ROOT}/dotnet"
    local ok_marker="${root}/.xianix-ok-${version}"

    if [ ! -f "${ok_marker}" ]; then
        if [ ! -f "${DOTNET_INSTALL_SCRIPT}" ]; then
            log "WARNING: ${DOTNET_INSTALL_SCRIPT} missing from the image — cannot install dotnet ${version}."
            return 1
        fi

        (
            flock -w 600 9 || { log "WARNING: timed out waiting for the provisioning lock."; exit 1; }
            # Re-check under the lock — a concurrent container may have just installed it.
            [ -f "${ok_marker}" ] && exit 0

            log "Installing dotnet ${version} into ${root} (first use on this volume)…"
            mkdir -p "${root}"
            local selector=(--channel "${version}")
            [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] && selector=(--version "${version}")

            if bash "${DOTNET_INSTALL_SCRIPT}" "${selector[@]}" \
                    --install-dir "${root}" --no-path >&2; then
                touch "${ok_marker}"
            else
                log "WARNING: dotnet ${version} install failed."
                exit 1
            fi
        ) 9>"${LOCK_FILE}" || return 1
    else
        log "dotnet ${version} already provisioned (cache hit)."
    fi

    mark_used "dotnet" "${version}"

    if [ "${DOTNET_ENV_EMITTED}" -eq 0 ]; then
        DOTNET_ENV_EMITTED=1
        emit_env "export DOTNET_ROOT=\"${root}\""
        emit_env "export PATH=\"${root}:\${PATH}\""
        emit_env "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
        emit_env "export DOTNET_NOLOGO=1"
        # Persist the NuGet package cache next to the SDK so restores are also
        # cached tenant-wide (the global-packages folder is concurrency-safe).
        emit_env "export NUGET_PACKAGES=\"${RUNTIMES_ROOT}/cache/nuget\""
        mkdir -p "${RUNTIMES_ROOT}/cache/nuget" 2>/dev/null || true
    fi
    PROVISIONED+=("dotnet ${version}")
    return 0
}

# ── Provider: node ────────────────────────────────────────────────────────────
# The image ships Node 20 globally; a manifest entry only makes sense for a
# different version. Each version gets its own dir, prepended to PATH (last
# manifest entry wins if several are requested — they can still be addressed
# by absolute path).
node_arch() {
    case "$(uname -m)" in
        x86_64)          echo "x64" ;;
        aarch64 | arm64) echo "arm64" ;;
        *)               echo "" ;;
    esac
}

# "22" or "22.11" → newest matching full version from the nodejs.org index.
resolve_node_version() {
    local version="$1"
    if [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        printf '%s' "${version}"
        return 0
    fi
    curl -fsSL --max-time 30 "${NODE_DIST_BASE}/index.json" 2>/dev/null \
        | jq -r --arg prefix "v${version}." \
            'map(select(.version | startswith($prefix))) | first | .version // empty' 2>/dev/null \
        | sed 's/^v//'
}

provision_node() {
    local requested="$1"
    local arch version dir
    arch="$(node_arch)"
    if [ -z "${arch}" ]; then
        log "WARNING: unsupported architecture '$(uname -m)' for node — skipping."
        return 1
    fi

    version="$(resolve_node_version "${requested}")"
    if [ -z "${version}" ]; then
        log "WARNING: could not resolve node version '${requested}' — skipping."
        return 1
    fi

    dir="${RUNTIMES_ROOT}/node-${version}"

    if [ ! -f "${dir}/.xianix-ok" ]; then
        (
            flock -w 600 9 || { log "WARNING: timed out waiting for the provisioning lock."; exit 1; }
            [ -f "${dir}/.xianix-ok" ] && exit 0

            log "Installing node ${version} into ${dir} (first use on this volume)…"
            local tmp tarball
            tmp="$(mktemp -d "${RUNTIMES_ROOT}/.tmp-node-XXXXXX")" || exit 1
            tarball="node-v${version}-linux-${arch}.tar.gz"

            if curl -fsSL --max-time 600 "${NODE_DIST_BASE}/v${version}/${tarball}" \
                    | tar -xz -C "${tmp}" --strip-components=1; then
                touch "${tmp}/.xianix-ok"
                rm -rf "${dir}" 2>/dev/null
                mv "${tmp}" "${dir}"
            else
                log "WARNING: node ${version} download/extract failed."
                rm -rf "${tmp}" 2>/dev/null
                exit 1
            fi
        ) 9>"${LOCK_FILE}" || return 1
    else
        log "node ${version} already provisioned (cache hit)."
    fi

    mark_used "node" "${version}"
    emit_env "export PATH=\"${dir}/bin:\${PATH}\""
    PROVISIONED+=("node ${version}")
    return 0
}

# ── Main ──────────────────────────────────────────────────────────────────────
requests="$(collect_requests)"
if [ -z "${requests}" ]; then
    log "No plugin declared runtime requirements — nothing to provision."
    exit 0
fi

log "Runtime cache root: ${RUNTIMES_ROOT}"

while IFS=$'\t' read -r name version; do
    [ -n "${name}" ] || continue

    if ! valid_version "${version}"; then
        log "WARNING: invalid version '${version}' for runtime '${name}' — skipping."
        continue
    fi

    case "${name}" in
        dotnet) provision_dotnet "${version}" || true ;;
        node)   provision_node "${version}"   || true ;;
        *)      log "WARNING: unsupported runtime '${name}' (supported: dotnet, node) — skipping." ;;
    esac
done <<< "${requests}"

if [ "${#PROVISIONED[@]}" -gt 0 ]; then
    summary="$(printf '%s; ' "${PROVISIONED[@]}")"
    summary="${summary%; }"
    # Surfaced in the prompt's host-context block (see host_context.py) so the
    # agent knows these tools are on PATH without probing.
    emit_env "export XIANIX_PROVISIONED_RUNTIMES=\"${summary}\""
    log "Provisioned runtimes: ${summary}"
fi

exit 0

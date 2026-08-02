#!/usr/bin/env bash
# Provision the language runtimes a run needs, using mise (https://mise.jdx.dev)
# as the installer.
#
# Runtimes are declared with standard, tool-agnostic files rather than a
# Xianix-specific manifest, and come from two sources that are merged:
#
#   1. The repository being worked on — authoritative, because the repo (not the
#      plugin) knows which SDK it builds with. mise reads the idiomatic version
#      files a repo already has: `global.json`, `.nvmrc`, `.node-version`,
#      `.python-version`, `go.mod`, `.java-version`, a `mise.toml`, … Most repos
#      therefore need no new file at all. Disable with XIANIX_RUNTIME_AUTODETECT=0.
#
#   2. Plugin manifests — a `.tool-versions` file at the plugin root, for plugins
#      that must build/run code (e.g. a unit-test writer verifying with
#      `dotnet test`) against repos that declare nothing:
#
#        dotnet 9.0
#        node   22.11.0
#
# Plugin manifests are written into a generated mise *global* config, which is
# the lowest-precedence config file — so whatever the repo declares wins on
# conflict, and the plugin entry acts as the fallback it should be.
#
# After plugin install, run_prompt.sh calls this script. It installs whatever is
# missing (user-space only — the container runs as non-root with
# no-new-privileges, so no apt) into the runtime cache, and writes `export` lines
# to the env file given as $1. run_prompt.sh sources that file so the runtimes
# are on PATH for Claude Code's Bash tool.
#
# Cache location (RUNTIMES_ROOT):
#   /workspace/runtimes            — the shared per-tenant volume mounted by the
#                                    control plane; one SDK download serves every
#                                    repo and plugin of the tenant.
#   ${REPO_DIR}/xianix-runtimes    — fallback when no runtime volume is mounted
#                                    (local `docker run`, older control planes).
#
# Cache contract:
#   * MISE_DATA_DIR/MISE_CACHE_DIR/MISE_STATE_DIR all live under RUNTIMES_ROOT, so
#     every download, install and bit of metadata lands on the volume. mise keeps
#     one install dir per tool+version, and (for dotnet) a shared DOTNET_ROOT that
#     hosts SDK versions side by side the way the muxer expects.
#   * Installs run under one flock (${RUNTIMES_ROOT}/.provision.lock) so concurrent
#     containers — possibly serving different repos of the tenant — never clobber
#     each other. A fully warm cache skips the lock entirely.
#   * Every resolved runtime touches ${RUNTIMES_ROOT}/.meta/<tool>@<version>.last-used
#     so maintain_volume.sh can prune runtimes nobody has used in a while.
#
# Security: mise runs with `safe` mode on, so no config in a plugin or repository
# can execute code, run a postinstall hook, or inject environment variables — it
# can only declare tool versions. Plugin manifests are additionally checked
# against an allow-list of runtime names and a strict version pattern, and the
# code-executing backends (asdf/vfox) plus the compile-from-source ones are
# disabled outright, so nothing here can be pointed at an arbitrary source.
#
# Best-effort like plugin install: a failed runtime install logs a warning and is
# skipped; the script always exits 0 so provisioning can never fail a run.
set -uo pipefail

log() { echo "[runtimes] $*" >&2; }

ENV_FILE="${1:-}"
if [ -z "${ENV_FILE}" ]; then
    log "FATAL: usage: provision_runtimes.sh <env_out_file>"
    exit 0
fi
: > "${ENV_FILE}" 2>/dev/null || { log "WARNING: cannot write env file '${ENV_FILE}' — skipping provisioning."; exit 0; }
emit_env() { printf '%s\n' "$1" >> "${ENV_FILE}"; }

if ! command -v mise >/dev/null 2>&1; then
    log "WARNING: mise is not on PATH — skipping provisioning."
    exit 0
fi

MANIFEST_NAME=".tool-versions"
LOCK_WAIT_SECONDS="${XIANIX_RUNTIME_LOCK_WAIT_SECONDS:-600}"
AUTODETECT="${XIANIX_RUNTIME_AUTODETECT:-1}"

# Runtimes a plugin manifest may ask for, and the tools whose idiomatic version
# files are honoured in the repo. Language toolchains only — the executor
# provisions runtimes, not arbitrary CLI tools (those belong in the image).
ALLOWED_TOOLS="${XIANIX_RUNTIME_ALLOWED_TOOLS:-bun deno dotnet elixir erlang go java kotlin node python ruby rust scala swift zig}"

# Repository files mise can derive a toolchain from — one per idiomatic version
# file of an allow-listed runtime. Finding one of these is what makes
# autodetection worth a mise invocation; mise does the actual parsing.
# Deliberately excludes package.json: mise's node plugin reads `.nvmrc` and
# `.node-version` only — not `engines.node`, not `volta` — and package.json's
# `packageManager` field resolves to pnpm/yarn/npm, which are package managers
# rather than runtimes and so are not allow-listed. Listing it here would fire a
# mise run for every JS repo and always resolve nothing.
REPO_DECLARATION_FILES=(
    mise.toml .mise.toml mise/config.toml .mise/config.toml
    .config/mise.toml .config/mise/config.toml
    .tool-versions
    global.json .nvmrc .node-version .python-version .python-versions
    .ruby-version Gemfile .java-version .sdkmanrc .go-version go.mod
    rust-toolchain.toml .swift-version .bun-version .deno-version
    .exenv-version .zig-version
)

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

# ── mise configuration ────────────────────────────────────────────────────────
# Everything mise persists is redirected onto the runtime volume. The security
# knobs are set here rather than baked into the image so this script is the one
# place that defines the sandbox, and they are re-emitted into the env file so
# any `mise` the agent runs later inherits the same restrictions.
GENERATED_CONFIG="/tmp/xianix-mise-global-${EXECUTION_ID:-$$}.toml"

export MISE_DATA_DIR="${RUNTIMES_ROOT}/mise"
export MISE_CACHE_DIR="${RUNTIMES_ROOT}/mise-cache"
export MISE_STATE_DIR="${RUNTIMES_ROOT}/mise-state"
export MISE_GLOBAL_CONFIG_FILE="${GENERATED_CONFIG}"
export MISE_GLOBAL_CONFIG_ROOT="${RUNTIMES_ROOT}"
# Hard code-execution boundary: configs we don't control (a plugin's, a repo's)
# can declare tool versions and nothing else — no tasks, no hooks, no [env]
# injection into the Claude Code process, no overriding these settings.
export MISE_SAFE=1
export MISE_YES=1
# asdf/vfox plugins are arbitrary shell fetched from git; cargo/npm/pipx/gem/spm
# compile or install from language package registries. None of them are how a
# language runtime should arrive, and all of them widen the supply chain.
# `dotnet` and `go` are deliberately absent: those names are core mise plugins as
# well as backends, and disabling them would take the runtimes with them.
export MISE_DISABLE_BACKENDS="asdf,vfox,cargo,npm,pipx,gem,spm"
export MISE_IDIOMATIC_VERSION_FILE_ENABLE_TOOLS="${ALLOWED_TOOLS// /,}"
export MISE_HTTP_TIMEOUT="${XIANIX_RUNTIME_HTTP_TIMEOUT:-120}"
# Nothing above /workspace can hold config; stop the upward config walk there so
# a worktree lookup never scans the container root.
export MISE_CEILING_PATHS="/workspace"

# ── Environment handed to the agent ───────────────────────────────────────────
# Emitted on every exit path — including "nothing to provision", which is exactly
# when the fallback hook below matters most.
FALLBACK="${XIANIX_RUNTIME_FALLBACK:-1}"
HOOK_FILE="/tmp/xianix-runtime-hook-${EXECUTION_ID:-$$}.sh"

# Binaries whose name differs from the mise tool that provides them. A lookup
# table rather than per-runtime logic: without it a Rust repo's `cargo build` and
# a Java repo's `javac` fall through even though the runtime is installable.
BIN_ALIASES="cargo:rust rustc:rust rustup:rust javac:java jar:java"

emit_agent_mise_env() {
    # Hand the agent the same mise sandbox, so a `mise install`/`mise exec` from
    # the Bash tool reuses the tenant cache and stays inside the same restrictions.
    emit_env "export MISE_DATA_DIR=\"${MISE_DATA_DIR}\""
    emit_env "export MISE_CACHE_DIR=\"${MISE_CACHE_DIR}\""
    emit_env "export MISE_STATE_DIR=\"${MISE_STATE_DIR}\""
    emit_env "export MISE_SAFE=1"
    emit_env "export MISE_YES=1"
    emit_env "export MISE_DISABLE_BACKENDS=\"${MISE_DISABLE_BACKENDS}\""

    [ "${FALLBACK}" = "1" ] || return 0

    # Last resort for a repository that needs a runtime but declares no version.
    # The eager pass above gives it nothing, and — unlike node and python, which
    # the image ships — that is a hard `command not found` rather than a silent
    # fallback. mise installs on first use instead (`exec_auto_install`), so wire
    # that to bash's command-not-found hook. BASH_ENV is the only entry point that
    # reaches the agent, which runs every tool call as a fresh `bash -c`.
    #
    # Gated on the same allow-list as the eager path, so a typo or a non-runtime
    # binary is refused immediately without a registry lookup or a download.
    {
        printf '%s\n' "# Generated by provision_runtimes.sh — do not edit."
        printf 'XIANIX_FALLBACK_TOOLS=%q\n'   "${ALLOWED_TOOLS}"
        printf 'XIANIX_FALLBACK_ALIASES=%q\n' "${BIN_ALIASES}"
        cat <<'HOOK'
command_not_found_handle() {
    local cmd="$1" tool="$1" pair
    for pair in ${XIANIX_FALLBACK_ALIASES}; do
        [ "${pair%%:*}" = "${cmd}" ] && { tool="${pair#*:}"; break; }
    done
    case " ${XIANIX_FALLBACK_TOOLS} " in
        *" ${tool} "*)
            echo "[runtimes] '${cmd}' is not installed and no version file declares it — installing ${tool}@latest into the tenant cache. Pin a version (e.g. global.json, .nvmrc) to install it up front instead." >&2
            mise exec "${tool}@latest" -- "$@"
            return $?
            ;;
    esac
    echo "bash: ${cmd}: command not found" >&2
    return 127
}
HOOK
    } > "${HOOK_FILE}" 2>/dev/null || {
        log "WARNING: cannot write ${HOOK_FILE} — runtime fallback disabled."
        return 0
    }
    emit_env "export BASH_ENV=\"${HOOK_FILE}\""
}

# ── Validation of plugin-declared entries ─────────────────────────────────────
# Tool names are plain registry short-names. Rejecting ':' blocks mise's explicit
# backend syntax (`cargo:foo`, `asdf:<git-url>`, …) outright, so a manifest can
# never point the installer at a source of its choosing; the allow-list then
# narrows what is left to actual language runtimes.
valid_tool() {
    [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$ ]] || return 1
    local allowed
    for allowed in ${ALLOWED_TOOLS}; do
        [ "$1" = "${allowed}" ] && return 0
    done
    return 1
}

# Versions become path components and URL segments. Digits/letters/dots/dashes
# (9.0, 9.0.203, 22.11.0, lts, latest) plus mise's harmless `prefix:` scope.
# This rejects `ref:` and `path:` (compile from a vcs ref / run a binary from an
# arbitrary path) and, by excluding '/', any traversal.
valid_version() { [[ "$1" =~ ^(prefix:)?[A-Za-z0-9][A-Za-z0-9.+-]{0,31}$ ]]; }

# ── Collect plugin manifests ──────────────────────────────────────────────────
# One "tool<TAB>version" line per entry, deduplicated. `.tool-versions` allows
# several fallback versions per line; only the first is meaningful here.
collect_plugin_requests() {
    local install_paths manifest
    install_paths=$(claude plugin list --json 2>/dev/null \
        | jq -r '.[].installPath // empty' 2>/dev/null) || true

    while IFS= read -r plugin_path; do
        [ -n "${plugin_path}" ] && [ -d "${plugin_path}" ] || continue
        manifest="${plugin_path}/${MANIFEST_NAME}"
        [ -f "${manifest}" ] || continue
        sed -e 's/#.*$//' "${manifest}" 2>/dev/null | awk 'NF >= 2 { print $1 "\t" $2 }'
    done <<< "${install_paths}" | sort -u
}

REQUESTED=()
{
    echo "# Generated by provision_runtimes.sh from the .tool-versions of installed"
    echo "# plugins. Lowest-precedence config, so repository declarations override it."
    echo "[tools]"
} > "${GENERATED_CONFIG}" 2>/dev/null || {
    log "WARNING: cannot write ${GENERATED_CONFIG} — plugin manifests will be ignored."
}

while IFS=$'\t' read -r tool version; do
    [ -n "${tool}" ] || continue
    if ! valid_tool "${tool}"; then
        log "WARNING: runtime '${tool}' is not allow-listed (allowed: ${ALLOWED_TOOLS}) — skipping."
        continue
    fi
    if ! valid_version "${version}"; then
        log "WARNING: invalid version '${version}' for runtime '${tool}' — skipping."
        continue
    fi
    printf '%s = "%s"\n' "${tool}" "${version}" >> "${GENERATED_CONFIG}" 2>/dev/null || true
    REQUESTED+=("${tool}@${version}")
done <<< "$(collect_plugin_requests)"

# ── Decide where to resolve from ──────────────────────────────────────────────
# Running mise inside the worktree is what makes the repo's own version files
# count. With autodetection off (or no worktree) we resolve from an empty scratch
# directory instead, so only the generated global config applies.
REPO_DECLARATIONS=""
CONTEXT_DIR=""
if [ "${AUTODETECT}" = "1" ] && [ -n "${WORK_DIR:-}" ] && [ -d "${WORK_DIR}" ]; then
    CONTEXT_DIR="${WORK_DIR}"
    for _file in "${REPO_DECLARATION_FILES[@]}"; do
        [ -f "${WORK_DIR}/${_file}" ] || continue
        REPO_DECLARATIONS="${REPO_DECLARATIONS}${REPO_DECLARATIONS:+, }${_file}"
    done
else
    CONTEXT_DIR="$(mktemp -d "/tmp/xianix-mise-ctx-XXXXXX" 2>/dev/null)" || CONTEXT_DIR="/tmp"
fi

if [ "${#REQUESTED[@]}" -eq 0 ] && [ -z "${REPO_DECLARATIONS}" ]; then
    emit_agent_mise_env
    if [ "${FALLBACK}" = "1" ]; then
        log "No plugin manifest and no repository version file — nothing to install up front; runtimes will be fetched on first use."
    else
        log "No plugin manifest and no repository version file — nothing to provision."
    fi
    exit 0
fi

[ "${#REQUESTED[@]}" -gt 0 ] && log "Plugin-declared runtimes: ${REQUESTED[*]}"
[ -n "${REPO_DECLARATIONS}" ] && log "Repository runtime declarations: ${REPO_DECLARATIONS}"
log "Runtime cache root: ${RUNTIMES_ROOT}"

cd "${CONTEXT_DIR}" || { log "WARNING: cannot enter '${CONTEXT_DIR}' — skipping provisioning."; exit 0; }

# ── Install ───────────────────────────────────────────────────────────────────
# `mise ls --missing` lists declared-but-not-installed tools, so a warm cache
# costs one process spawn and never contends on the provisioning lock.
missing_count="$(mise ls --missing --json 2>/dev/null | jq -r 'length' 2>/dev/null)" || missing_count=""

if [ "${missing_count}" = "0" ]; then
    log "All declared runtimes already provisioned (cache hit)."
else
    (
        flock -w "${LOCK_WAIT_SECONDS}" 9 || { log "WARNING: timed out waiting for the provisioning lock."; exit 1; }
        log "Installing missing runtimes into ${MISE_DATA_DIR} …"
        mise install >&2
    ) 9>"${LOCK_FILE}" \
        || log "WARNING: one or more runtime installs failed — continuing with whatever succeeded."
fi

# ── Emit the environment ──────────────────────────────────────────────────────
# `mise env` prints the exact export lines we need (PATH, DOTNET_ROOT, …) for the
# tools resolved above; run_prompt.sh sources them before launching Claude Code.
_env_err="$(mktemp 2>/dev/null || echo /dev/null)"
if ! mise env -s bash >> "${ENV_FILE}" 2>"${_env_err}"; then
    log "WARNING: 'mise env' failed — continuing without runtime env: $(tr '\n' ' ' < "${_env_err}" 2>/dev/null)"
fi
rm -f "${_env_err}" 2>/dev/null || true

# ── Record what actually landed ───────────────────────────────────────────────
# `mise ls --current` reports the concrete version behind each declaration along
# with whether it is installed, so the summary and the last-used markers key off
# what is really on disk rather than the fuzzy string that was requested.
# Anything that failed to install is reported with installed=false and left out.
PROVISIONED=()
while IFS=$'\t' read -r tool version; do
    [ -n "${tool}" ] && [ -n "${version}" ] || continue
    touch "${RUNTIMES_ROOT}/.meta/${tool}@${version}.last-used" 2>/dev/null || true
    PROVISIONED+=("${tool} ${version}")
done < <(mise ls --current --json 2>/dev/null \
    | jq -r 'to_entries[] | .key as $t | (.value[0] // empty)
             | select(.installed == true) | "\($t)\t\(.version)"' 2>/dev/null)

if [ "${#PROVISIONED[@]}" -eq 0 ]; then
    emit_agent_mise_env
    log "WARNING: no runtime could be provisioned — the run continues with the image's built-in toolchain."
    exit 0
fi

# The .NET SDK restores into a global-packages folder; parking it on the volume
# next to the SDK makes restores cached tenant-wide (it is concurrency-safe).
if printf '%s\n' "${PROVISIONED[@]}" | grep -q '^dotnet '; then
    NUGET_DIR="${RUNTIMES_ROOT}/cache/nuget"
    mkdir -p "${NUGET_DIR}" 2>/dev/null || true
    emit_env "export NUGET_PACKAGES=\"${NUGET_DIR}\""
    emit_env "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    emit_env "export DOTNET_NOLOGO=1"
fi

emit_agent_mise_env

summary="$(printf '%s; ' "${PROVISIONED[@]}")"
summary="${summary%; }"
# Surfaced in the prompt's host-context block (see host_context.py) so the agent
# knows these tools are on PATH without probing.
emit_env "export XIANIX_PROVISIONED_RUNTIMES=\"${summary}\""
log "Provisioned runtimes: ${summary}"

exit 0

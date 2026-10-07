#!/usr/bin/env bash
# Generates and caches deterministic repository context for OpenCode runs.
#
#   * CLAUDE.md (orientation) — common agent convention; OpenCode often reads it
#   * .xianix/repomap.txt — file→symbol map
#
# Cached on the tenant volume by HEAD sha. Deterministic only (no LLM narrative).
# Usage: generate_context.sh <worktree_dir> <cache_dir>
set -uo pipefail

log() { echo "[context] $*" >&2; }

WT="${1:-}"
CACHE_DIR="${2:-}"

if [ -z "${WT}" ] || [ ! -d "${WT}" ]; then
    log "WARNING: worktree '${WT}' missing — skipping context generation."
    exit 0
fi
if [ -z "${CACHE_DIR}" ]; then
    log "WARNING: no cache dir provided — skipping context generation."
    exit 0
fi

MAX_REPOMAP_LINES=2000
MAX_OVERVIEW_LINES=40
README_MD="${CACHE_DIR}/CLAUDE.md"
REPOMAP="${CACHE_DIR}/repomap.txt"
SHA_FILE="${CACHE_DIR}/sha"

mkdir -p "${CACHE_DIR}" 2>/dev/null || { log "WARNING: cannot create cache dir '${CACHE_DIR}'."; exit 0; }

current_sha="$(git -C "${WT}" rev-parse HEAD 2>/dev/null || echo "nogit")"

# Detect a repo-owned CLAUDE.md up front (covers regular files, dirs, and symlinks — even
# broken ones, which `-e` alone would miss). When the tenant ships their own, we never inject
# AND we skip the expensive LLM pass: there is no point spending tokens on content that will
# never reach the worktree.
repo_has_own_claude=0
if [ -e "${WT}/CLAUDE.md" ] || [ -L "${WT}/CLAUDE.md" ]; then
    repo_has_own_claude=1
fi

# ── Detect language / build & test commands from marker files ────────────────
detect_stack() {
    local found=0
    _line() { printf '* %s\n' "$1"; found=1; }
    [ -f "${WT}/package.json" ]      && _line "Node.js — \`npm install\`, \`npm test\`, \`npm run build\` (see package.json scripts)."
    [ -f "${WT}/pnpm-lock.yaml" ]    && _line "pnpm workspace — \`pnpm install\`, \`pnpm test\`."
    [ -f "${WT}/requirements.txt" ]  && _line "Python — \`pip install -r requirements.txt\`; tests via \`pytest\`."
    [ -f "${WT}/pyproject.toml" ]    && _line "Python (pyproject) — \`pip install -e .\` / \`uv sync\`; tests via \`pytest\`."
    ls "${WT}"/*.sln >/dev/null 2>&1 && _line ".NET solution — \`dotnet build\`, \`dotnet test\`."
    ls "${WT}"/**/*.csproj >/dev/null 2>&1 && _line ".NET project(s) present — \`dotnet build\`, \`dotnet test\`."
    [ -f "${WT}/go.mod" ]            && _line "Go — \`go build ./...\`, \`go test ./...\`."
    [ -f "${WT}/Cargo.toml" ]        && _line "Rust — \`cargo build\`, \`cargo test\`."
    [ -f "${WT}/pom.xml" ]           && _line "Java (Maven) — \`mvn -q verify\`."
    { [ -f "${WT}/build.gradle" ] || [ -f "${WT}/build.gradle.kts" ]; } && _line "Java/Kotlin (Gradle) — \`./gradlew build test\`."
    [ -f "${WT}/Gemfile" ]           && _line "Ruby — \`bundle install\`, \`bundle exec rspec\`."
    [ -f "${WT}/Makefile" ]          && _line "Makefile present — inspect targets with \`make help\` / \`grep -E '^[a-z].*:' Makefile\`."
    [ "${found}" -eq 0 ] && printf '* No standard build/test marker files detected — inspect the repo root.\n'
}

# ── Compact top-level layout (depth 2, noise dirs pruned) ────────────────────
detect_layout() {
    ( cd "${WT}" && find . -maxdepth 2 -type d \
        \( -name .git -o -name node_modules -o -name dist -o -name build \
           -o -name bin -o -name obj -o -name .venv -o -name venv \
           -o -name vendor -o -name target -o -name .next -o -name .idea \) -prune -o \
        -type d -print 2>/dev/null \
      | sed 's|^\./||' | grep -v '^\.$' | sort | head -n 60 )
}

# ── README excerpt (first non-empty lines) ───────────────────────────────────
detect_overview() {
    local readme
    for readme in README.md README.rst README README.txt readme.md; do
        if [ -f "${WT}/${readme}" ]; then
            grep -v '^[[:space:]]*$' "${WT}/${readme}" | head -n "${MAX_OVERVIEW_LINES}"
            return 0
        fi
    done
    printf '_No README found at the repository root._\n'
}

# ── Symbol map via ctags, with a source-file listing as a robust fallback ────
# universal-ctags (shipped in the image) supports `-x -R --exclude`; if a different ctags
# variant is on PATH and produces nothing, or ctags is absent, we degrade to a file inventory
# so the agent at least gets the source layout instead of an empty map.
generate_repomap() {
    local symbols=""
    if command -v ctags >/dev/null 2>&1; then
        # `-x` emits a stable cross-reference: "name kind line file pattern".
        # Reformat to "file: name (kind)" so the model gets a path-anchored symbol map.
        symbols="$( ( cd "${WT}" && ctags -x -R \
            --exclude=.git --exclude=node_modules --exclude=dist --exclude=build \
            --exclude=bin --exclude=obj --exclude=.venv --exclude=venv \
            --exclude=vendor --exclude=target --exclude=.next . 2>/dev/null ) \
          | awk 'NF>=4 {print $4": "$1" ("$2")"}' \
          | sort -u | head -n "${MAX_REPOMAP_LINES}" )"
    fi

    if [ -n "${symbols}" ]; then
        printf '%s\n' "${symbols}"
        return 0
    fi

    # Fallback: enumerate source files so the model at least has the file inventory.
    ( cd "${WT}" && find . -type f \
        \( -name '*.ts' -o -name '*.tsx' -o -name '*.js' -o -name '*.jsx' \
           -o -name '*.py' -o -name '*.cs' -o -name '*.go' -o -name '*.rs' \
           -o -name '*.java' -o -name '*.kt' -o -name '*.rb' -o -name '*.php' \) \
        2>/dev/null | sed 's|^\./||' | sort | head -n "${MAX_REPOMAP_LINES}" )
}

# (no LLM narrative in this image — deterministic context only)

# ── Reuse cache when HEAD hasn't moved, else regenerate ──────────────────────
cached_sha="$(cat "${SHA_FILE}" 2>/dev/null || echo "")"
if [ "${cached_sha}" = "${current_sha}" ] && [ -s "${README_MD}" ]; then
    log "Reusing cached context for HEAD ${current_sha} (cache hit)."
else
    log "Generating context for HEAD ${current_sha} (cache miss)."

    {
        printf '# Repository Context\n\n'
        printf '_Auto-generated by the Xianix Executor — a deterministic, cached snapshot to help you\n'
        printf 'orient quickly. Prefer this over re-scanning the whole tree. Regenerated only when the\n'
        printf 'branch HEAD changes._\n\n'
        printf '## Overview\n\n'
        detect_overview
        printf '\n## Detected stack & commands\n\n'
        detect_stack
        printf '\n## Top-level layout\n\n```\n'
        detect_layout
        printf '```\n\n## Symbol map\n\n'
        printf 'A compact file-to-symbol map lives at `.xianix/repomap.txt`. Read it to locate code by\n'
        printf 'symbol before grepping — it lists the functions/classes defined in each source file.\n'
    } > "${README_MD}.tmp" 2>/dev/null && mv "${README_MD}.tmp" "${README_MD}"

    generate_repomap > "${REPOMAP}.tmp" 2>/dev/null || true
    if [ -s "${REPOMAP}.tmp" ]; then mv "${REPOMAP}.tmp" "${REPOMAP}"; else rm -f "${REPOMAP}.tmp"; fi

    printf '%s' "${current_sha}" > "${SHA_FILE}" 2>/dev/null || true
fi

# ── Inject into the worktree (respect tenant content; keep git status clean) ─
# Resolve the git dir absolutely so the exclude write doesn't depend on the caller's cwd, and
# so it targets the worktree-specific exclude file when WT is a linked worktree.
git_dir="$(git -C "${WT}" rev-parse --absolute-git-dir 2>/dev/null || echo "")"
add_exclude() {
    [ -z "${git_dir}" ] && return 0
    mkdir -p "${git_dir}/info" 2>/dev/null || true
    local ex="${git_dir}/info/exclude"
    grep -qxF "$1" "${ex}" 2>/dev/null || printf '%s\n' "$1" >> "${ex}" 2>/dev/null || true
}

if [ -s "${REPOMAP}" ]; then
    mkdir -p "${WT}/.xianix" 2>/dev/null || true
    cp "${REPOMAP}" "${WT}/.xianix/repomap.txt" 2>/dev/null || true
    add_exclude ".xianix/"
fi

if [ -s "${README_MD}" ]; then
    if [ "${repo_has_own_claude}" = "1" ]; then
        log "Repo already ships a CLAUDE.md — leaving it untouched (tenant content wins)."
    elif cp "${README_MD}" "${WT}/CLAUDE.md" 2>/dev/null; then
        add_exclude "CLAUDE.md"
        log "Injected generated CLAUDE.md into the worktree."
    else
        log "WARNING: could not write CLAUDE.md into the worktree — continuing without it."
    fi
fi

log "Context preparation complete."
exit 0

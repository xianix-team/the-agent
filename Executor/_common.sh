#!/usr/bin/env bash
# Shared helpers sourced by prepare_repo.sh and run_prompt.sh.
#
# Responsibilities:
#   * Re-export dashed env var aliases (bash can't use dashes in names).
#   * Provide the `log` helper used everywhere.
#   * Extract the framework-managed inputs (repository-url / platform / git-ref)
#     from XIANIX_INPUTS so both scripts agree on what they describe.
#   * Define REPO_DIR / WORK_DIR / GIT_CRED_FILE as canonical paths.
#   * Provide `configure_credentials` so either script can stand on its own
#     (in `execute`-only mode prepare_repo.sh hasn't run, but git fetch /
#     worktree creation in run_prompt.sh still need credentials).
set -euo pipefail

# Re-export dashed env vars as underscored aliases. E.g. GITHUB-TOKEN → GITHUB_TOKEN.
# Use null-delimited env output to safely handle values with newlines or '='.
while IFS= read -r -d '' entry; do
  key="${entry%%=*}"
  value="${entry#*=}"
  [[ "$key" == *-* ]] && export "${key//-/_}=${value}"
done < <(env -0)

log() { echo "$@" >&2; }

# ── Inputs (extracted from XIANIX_INPUTS) ────────────────────────────────────
# `repository-url`, `platform`, and `git-ref` are framework-managed structural fields
# auto-injected by the agent from the execution-level `platform` / `repository` block
# in rules.json (or synthesized by the chat onboarding tool). They are not authored
# under `use-inputs` — the agent serialises them under these canonical kebab-case keys.
# Note: do NOT write `${XIANIX_INPUTS:-{}}` — bash parses the first `}` as the
# end of the parameter expansion, leaving a stray `}` appended to the value
# (which then breaks jq with "Unmatched '}'"). Use `:=` to assign-if-unset
# instead; bash handles the braces correctly there.
: "${XIANIX_INPUTS:={\}}"
REPOSITORY_URL=$(echo "${XIANIX_INPUTS}" | jq -r '."repository-url" // empty')
PLATFORM=$(echo "${XIANIX_INPUTS}"       | jq -r '.platform // empty')
GIT_REF=$(echo "${XIANIX_INPUTS}"        | jq -r '."git-ref" // empty')

if [ -n "${REPOSITORY_URL}" ] && [ -z "${PLATFORM}" ]; then
    PLATFORM="github"
fi

export REPOSITORY_URL PLATFORM GIT_REF

# ── Workspace paths ──────────────────────────────────────────────────────────
REPO_DIR="/workspace/repo"
WORK_DIR="/workspace/exec-${EXECUTION_ID}"
GIT_CRED_FILE="/tmp/.git-credentials"
export REPO_DIR WORK_DIR GIT_CRED_FILE

# ── Platform-specific credential & URL setup ─────────────────────────────────
# Strip surrounding whitespace/CR/LF from a token. Vault-stored secrets often
# arrive with a trailing newline; if we drop that into a credential URL or HTTP
# header the request becomes malformed and auth silently fails.
_trim_token() {
    local t="$1"
    t="${t#"${t%%[![:space:]]*}"}"
    t="${t%"${t##*[![:space:]]}"}"
    printf '%s' "$t"
}

# Configure git Basic auth for a host via http.<url>.extraHeader. This avoids
# URL-encoding pitfalls in credential.helper=store: PATs with `@`, `:`, `/`,
# `=`, `+`, etc. don't need any escaping when sent as an HTTP header.
_set_basic_auth_header() {
    local url_prefix="$1" user="$2" pass="$3"
    local b64
    b64=$(printf '%s:%s' "$user" "$pass" | base64 | tr -d '\n')
    git config --global "http.${url_prefix}.extraHeader" "Authorization: Basic ${b64}"
}

configure_credentials() {
    : > "${GIT_CRED_FILE}"

    case "${PLATFORM}" in
        github)
            if [ -n "${GITHUB_TOKEN:-}" ]; then
                local gh_token
                gh_token=$(_trim_token "${GITHUB_TOKEN}")
                printf 'https://x-access-token:%s@github.com\n' "${gh_token}" >> "${GIT_CRED_FILE}"
                export GH_TOKEN="${gh_token}"
            fi
            ;;
        azuredevops)
            if [ -z "${AZURE_DEVOPS_TOKEN:-}" ]; then
                log "FATAL: AZURE_DEVOPS_TOKEN is required when platform=azuredevops."
                exit 1
            fi
            local ado_token
            ado_token=$(_trim_token "${AZURE_DEVOPS_TOKEN}")
            if [ -z "${ado_token}" ]; then
                log "FATAL: AZURE_DEVOPS_TOKEN is empty after trimming whitespace."
                exit 1
            fi
            REPOSITORY_URL=$(printf '%s' "${REPOSITORY_URL}" | sed -E 's|^https://[^@/]+@|https://|')

            # Configure http.extraHeader (the Azure DevOps-recommended Basic
            # auth approach) for both the modern dev.azure.com host and the
            # legacy {org}.visualstudio.com host. Username is empty per ADO
            # convention (token alone authenticates).
            _set_basic_auth_header "https://dev.azure.com/" "" "${ado_token}"
            if [[ "${REPOSITORY_URL}" =~ ^https://([^./]+)\.visualstudio\.com ]]; then
                _set_basic_auth_header \
                    "https://${BASH_REMATCH[1]}.visualstudio.com/" "" "${ado_token}"
            fi
            ;;
        *)
            log "WARNING: Unknown platform '${PLATFORM}' — no credentials configured."
            ;;
    esac

    if [ -s "${GIT_CRED_FILE}" ]; then
        chmod 600 "${GIT_CRED_FILE}"
        git config --global credential.helper "store --file=${GIT_CRED_FILE}"
    fi
}

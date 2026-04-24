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
configure_credentials() {
    : > "${GIT_CRED_FILE}"

    case "${PLATFORM}" in
        github)
            if [ -n "${GITHUB_TOKEN:-}" ]; then
                printf 'https://x-access-token:%s@github.com\n' "${GITHUB_TOKEN}" >> "${GIT_CRED_FILE}"
                export GH_TOKEN="${GITHUB_TOKEN}"
            fi
            ;;
        azuredevops)
            if [ -z "${AZURE_DEVOPS_TOKEN:-}" ]; then
                log "FATAL: AZURE_DEVOPS_TOKEN is required when platform=azuredevops."
                exit 1
            fi
            REPOSITORY_URL=$(printf '%s' "${REPOSITORY_URL}" | sed -E 's|^https://[^@/]+@|https://|')
            printf 'https://pat:%s@dev.azure.com\n' "${AZURE_DEVOPS_TOKEN}" >> "${GIT_CRED_FILE}"

            if [[ "${REPOSITORY_URL}" =~ ^https://([^./]+)\.visualstudio\.com ]]; then
                printf 'https://pat:%s@%s.visualstudio.com\n' "${AZURE_DEVOPS_TOKEN}" "${BASH_REMATCH[1]}" >> "${GIT_CRED_FILE}"
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

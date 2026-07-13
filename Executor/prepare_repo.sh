#!/usr/bin/env bash
# Prepare phase of the executor pipeline.
#
# Behaviour depends on XIANIX_MODE (default: prepare-and-execute):
#   * prepare              — bare clone only; no worktree, no plugins, no prompt.
#                            Used by the chat-driven OnboardRepository flow.
#   * prepare-and-execute  — bare clone + worktree (so run_prompt.sh can cd into
#                            it). Used by webhook flows and the chat
#                            RunClaudeCodeOnRepository flow.
#   * (any other value)    — caller is misusing this script; we still do the
#                            prepare-and-execute path for safety.
#
# Whenever REPOSITORY_URL is set, this script ALWAYS pulls the upstream default
# branch into the bare clone before any further git action — irrespective of
# whether the run was triggered by a webhook or by a user-conversational tool.
# This guarantees that both `git diff origin/<default>` and the worktree
# (`worktree add HEAD`) see the latest default-branch tip rather than whatever
# the bare clone happened to be left at by the previous execution.
#
# When REPOSITORY_URL is empty we either skip (mode=prepare) or create an empty
# workspace directory so a no-repo prompt run still has somewhere to cd into.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=_common.sh
source "${SCRIPT_DIR}/_common.sh"

XIANIX_MODE="${XIANIX_MODE:-prepare-and-execute}"

# ── Structured failure envelope for the prepare phase ────────────────────────
# When prepare fails (missing token, clone failure, unparseable inputs) the
# entrypoint's `&&` stops run_prompt.sh from running, so execute_plugin.py never
# emits its JSON envelope and the control plane is left with only stderr text and
# an exit code. Emit a minimal envelope to stdout on any non-zero exit so the
# consumer parses ONE shape for both phases (ContainerOutputParser ignores
# unknown fields and treats missing numeric fields as null, so the extra
# `phase` field and absent cost/token fields are safe). Success (exit 0) prints
# nothing — the run phase owns stdout from there on.
_emit_prepare_error() {
    local exit_code=$?
    [ "${exit_code}" -eq 0 ] && return 0
    local tid eid msg
    tid=$(printf '%s' "${TENANT_ID:-unknown}"    | jq -Rs .)
    eid=$(printf '%s' "${EXECUTION_ID:-unknown}" | jq -Rs .)
    msg=$(printf '%s' "prepare phase failed (exit ${exit_code})" | jq -Rs .)
    printf '{"tenant_id":%s,"execution_id":%s,"phase":"prepare","status":"error","error":%s}\n' \
        "${tid}" "${eid}" "${msg}"
    return "${exit_code}"
}
trap _emit_prepare_error EXIT

log "=== Xianix Executor — prepare phase ==="
log "Tenant:              ${TENANT_ID}"
log "Execution ID:        ${EXECUTION_ID}"
log "Mode:                ${XIANIX_MODE}"
log "Repository:          ${REPOSITORY_URL:-<none>}"
log "Platform:            ${PLATFORM:-<none>}"

# True when the bare repo has linked worktrees beyond itself, i.e. another
# concurrent execution is (or recently was) checked out against this volume.
# `git worktree list` prints the bare repo as the first entry and one line per
# linked worktree, so more than one entry means a peer worktree is registered.
_other_worktrees_registered() {
    local count
    count=$(git -C "${REPO_DIR}" worktree list --porcelain 2>/dev/null \
        | grep -c '^worktree ' || true)
    [ "${count:-0}" -gt 1 ]
}

ensure_bare_repo() {
    if [ -d "${REPO_DIR}" ] && { [ -d "${REPO_DIR}/.git" ] || [ -f "${REPO_DIR}/HEAD" ]; }; then
        log "--- Fetching into existing repo ---"
        if ! git -C "${REPO_DIR}" fetch --all --prune >&2; then
            log "--- Fetch failed; retrying once ---"
            if ! git -C "${REPO_DIR}" fetch --all --prune >&2; then
                # A destructive re-clone wipes the shared object store, which would
                # pull the rug out from under any worktree a concurrent execution is
                # actively using on this volume. Only re-clone when we're sure no peer
                # worktree is registered; otherwise proceed with the (possibly slightly
                # stale) refs already present rather than risk corrupting a live run.
                if _other_worktrees_registered; then
                    log "WARNING: fetch failed after retry, but other worktrees are live on this volume — skipping re-clone and proceeding with existing refs."
                else
                    log "--- Fetch failed after retry, re-cloning ---"
                    find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
                    git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2
                fi
            fi
        fi
    else
        log "--- Cloning repository (bare, first run for this tenant+repo) ---"
        find "${REPO_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        git clone --bare "${REPOSITORY_URL}" "${REPO_DIR}" >&2
    fi

    # Only prune worktree admin metadata that has been stale for a while. An
    # unqualified prune would delete the bookkeeping for a peer container's LIVE
    # worktree — that container's checkout lives in its own ephemeral filesystem
    # and is invisible here, so git would consider it "missing" and reap it
    # mid-run. The expiry window keeps genuinely orphaned (crashed-run) entries
    # collectable while protecting concurrent executions.
    git -C "${REPO_DIR}" worktree prune --expire=12.hours.ago >&2 2>/dev/null || true
}

# Always pull the upstream default branch into the bare clone. Why this is
# non-redundant with the `git fetch --all --prune` in `ensure_bare_repo`:
#
#   1. If the upstream renamed its default (e.g. master → main), `--prune`
#      deletes the old local head but the bare clone's `HEAD` symbolic ref
#      stays pointed at the now-missing branch — the next `worktree add HEAD`
#      then dies with "not a valid ref". We re-point HEAD at the live default
#      so the worktree path stays self-healing.
#
#   2. The previous fetch is best-effort across *all* refs and `set -e`-aware,
#      but a partial failure could leave the default branch stale. Re-fetching
#      it explicitly with `+refs/heads/<default>:refs/heads/<default>` makes
#      it a hard precondition for any execution that follows.
#
#   3. Plugins and free-form prompts routinely diff against `origin/<default>`
#      (PR reviews, base-branch comparisons, merge-base queries). Guaranteeing
#      a fresh default-branch tip here means that contract is honoured for both
#      webhook flows and user-conversational runs without each plugin having
#      to re-fetch on its own.
#
# Failures here are logged but non-fatal: the bare clone is already populated
# from the earlier fetch/clone, so the run can still proceed against a possibly-
# slightly-stale default rather than aborting the whole execution.
pull_default_branch() {
    log "--- Refreshing default branch from origin ---"

    local symref_line default_ref default_branch
    if ! symref_line=$(git -C "${REPO_DIR}" ls-remote --symref origin HEAD 2>/dev/null | head -n1); then
        log "WARNING: ls-remote failed — proceeding with the refs already in the bare clone."
        return 0
    fi

    # `ls-remote --symref origin HEAD` first line looks like:
    #   ref: refs/heads/main	HEAD
    default_ref="${symref_line%%$'\t'*}"
    default_ref="${default_ref#ref: }"
    default_branch="${default_ref#refs/heads/}"

    if [ -z "${default_branch}" ] || [ "${default_branch}" = "${default_ref}" ]; then
        log "WARNING: could not parse default branch from ls-remote output ('${symref_line}') — skipping refresh."
        return 0
    fi

    log "Default branch: ${default_branch}"

    # Force-update the local default-branch ref to match origin. The bare-clone
    # refspec already does this on `fetch --all`, but re-asserting it with an
    # explicit `+`-prefixed refspec converts a transient earlier-fetch failure
    # into a guaranteed up-to-date default branch (or a loud error here).
    if ! git -C "${REPO_DIR}" fetch origin \
            "+refs/heads/${default_branch}:refs/heads/${default_branch}" >&2; then
        log "WARNING: explicit fetch of default branch '${default_branch}' failed — continuing with current refs."
        return 0
    fi

    # Re-point local HEAD at the (possibly renamed) default branch so the
    # worktree path picks up the right tip, and so any consumer reading `HEAD`
    # from the bare clone gets the upstream's view of "default".
    git -C "${REPO_DIR}" symbolic-ref HEAD "refs/heads/${default_branch}" >&2 \
        || log "WARNING: failed to re-point HEAD at refs/heads/${default_branch}."
}

create_worktree() {
    # Purely generic git semantics, no task knowledge: always check out the
    # default branch (HEAD). Anything task-specific — e.g. a PR review needing
    # the PR's source branch — is the PLUGIN's job: it receives the task
    # context through the prompt and performs its own fetch/checkout inside the
    # worktree.
    log "--- Creating worktree for HEAD (default branch) ---"
    git -C "${REPO_DIR}" worktree add "${WORK_DIR}" HEAD --detach >&2
}

prepare_empty_workspace() {
    log "--- No repository — creating empty workspace ---"
    mkdir -p "${WORK_DIR}"
}

if [ -n "${REPOSITORY_URL}" ]; then
    configure_credentials
    ensure_bare_repo
    # Refresh the default branch unconditionally — both webhook executions and
    # user-conversational executions land here, so this single call is what
    # honours the "always pull the default branch before any git action"
    # contract for every entry point. Done before create_worktree so the
    # worktree resolves HEAD to the freshly-refreshed default tip.
    pull_default_branch

    # Best-effort volume housekeeping (git gc, stale plugin versions, old session
    # pointers). Never fatal — an unbounded volume is the failure mode we're
    # avoiding, not a reason to abort this run.
    "${SCRIPT_DIR}/maintain_volume.sh" "${REPO_DIR}" \
        || log "WARNING: volume maintenance failed — continuing."

    if [ "${XIANIX_MODE}" = "prepare" ]; then
        log "--- Skipping worktree (mode=prepare; bare clone only) ---"
    else
        create_worktree
    fi
else
    if [ "${XIANIX_MODE}" = "prepare" ]; then
        log "--- No repository and mode=prepare; nothing to onboard ---"
    else
        prepare_empty_workspace
    fi
fi

log "--- Prepare phase complete ---"

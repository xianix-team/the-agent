#!/usr/bin/env bash
# End-to-end tests for TheAgent deployed against a local Xians ACP.
#
# WHAT THIS DOES
# --------------
# Treats the whole deployment as a black box:
#
#   simulated webhook  →  Xians ACP  →  TheAgent (Integrator Workflow)
#        →  rules.json evaluation  →  ProcessingWorkflow
#        →  executor container (Docker)  →  Claude Code
#
# The harness sends GitHub- and Azure-DevOps-shaped webhook payloads to the
# platform webhook endpoint and then verifies each hop:
#
#   * the synchronous HTTP response ({"status":"success"|"ignored", ...}) proves
#     the agent is connected and the rules evaluated as expected;
#   * a new Docker container labelled xianix.managed=true whose XIANIX-INPUTS
#     references this run's unique fixture repo proves ProcessingWorkflow ran;
#   * the JSON envelope on the container's stdout, containing a random token
#     planted in the fixture repo, proves the executor cloned the repo and
#     Claude Code genuinely read it (impossible to fake without the full chain).
#
# Instead of a real GitHub/ADO repository, a tiny local git repo is generated
# per run and served to the executor container over `git daemon`
# (git://host.docker.internal:<port>/...). The e2e-* rules in
# TheAgent/Knowledge/rules.json declare platform "local", so the executor
# clones without credentials. Hermetic: no GitHub, no ADO, no tokens.
#
# PREREQUISITES
# -------------
#   * Local Xians ACP running, TheAgent deployed & registered (with the current
#     rules.json — the e2e-* blocks must be uploaded; restart the agent after
#     changing rules.json since it is an embedded resource).
#   * Docker reachable from this shell (the agent starts executor containers
#     on the same daemon).
#   * ANTHROPIC-API-KEY configured on the agent host (Tier 2 needs it).
#
# TEST TIERS
# ----------
#   Tier 1 (free): webhook routing — non-matching payloads answered "ignored",
#                  duplicate deliveries suppressed, no containers started.
#   Tier 2 (~$0.02): full chain for GitHub and ADO payload shapes — container
#                  runs, envelope status=completed, planted token echoed back.
#
# HOW TO RUN
# ----------
#   WEBHOOK_URL='http://localhost:5005/api/user/webhooks/builtin?apikeyId=...' ./e2e_test.sh
#   # or put WEBHOOK_URL in tests/e2e/.env (gitignored) and just: ./e2e_test.sh
#
#   SKIP_TIER2=1 ./e2e_test.sh       # routing tests only, no Claude cost
#   FIXTURE_PORT=9419 ./e2e_test.sh  # if 9418 is taken
#   KEEP_ARTIFACTS=1 ./e2e_test.sh   # don't rm the test containers/volumes
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Config ────────────────────────────────────────────────────────────────────
# A gitignored tests/e2e/.env can hold WEBHOOK_URL etc. Exported vars win.
if [ -f "${SCRIPT_DIR}/.env" ]; then
    while IFS='=' read -r k v; do
        [[ "${k}" =~ ^[A-Z_]+$ ]] || continue
        [ -z "${!k:-}" ] && export "${k}=${v}"
    done < "${SCRIPT_DIR}/.env"
fi

WEBHOOK_URL="${WEBHOOK_URL:-}"
FIXTURE_PORT="${FIXTURE_PORT:-9418}"
FIXTURE_HOST="${FIXTURE_HOST:-host.docker.internal}"   # how the executor container reaches this machine
CONTAINER_START_TIMEOUT="${CONTAINER_START_TIMEOUT:-90}"    # seconds to wait for the executor container to appear
CONTAINER_EXIT_TIMEOUT="${CONTAINER_EXIT_TIMEOUT:-420}"     # seconds to wait for it to finish
SKIP_TIER2="${SKIP_TIER2:-0}"
KEEP_ARTIFACTS="${KEEP_ARTIFACTS:-0}"

if [ -z "${WEBHOOK_URL}" ]; then
    echo "ERROR: WEBHOOK_URL is required (set env var or put it in tests/e2e/.env)." >&2
    exit 1
fi
command -v jq >/dev/null   || { echo "ERROR: jq is required." >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "ERROR: Docker is not reachable." >&2; exit 1; }

# ── Pretty output helpers (same conventions as Executor/tests) ────────────────
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'
    C_BOLD=$'\033[1m';   C_DIM=$'\033[2m';  C_RESET=$'\033[0m'
else
    C_GREEN=""; C_RED=""; C_YELLOW=""; C_BOLD=""; C_DIM=""; C_RESET=""
fi

TESTS_RUN=0
TESTS_FAILED=0
banner() { echo; echo "${C_BOLD}━━━ $* ━━━${C_RESET}"; }
t_pass() { TESTS_RUN=$((TESTS_RUN + 1)); echo "  ${C_GREEN}✔ PASS${C_RESET}  $*"; }
t_skip() { echo "  ${C_YELLOW}– SKIP${C_RESET}  $*"; }
t_fail() { TESTS_RUN=$((TESTS_RUN + 1)); TESTS_FAILED=$((TESTS_FAILED + 1)); echo "  ${C_RED}✘ FAIL${C_RESET}  $*"; }
note()   { echo "  ${C_DIM}$*${C_RESET}"; }

# ── Workspace / fixture setup ─────────────────────────────────────────────────
RUN_ID="$(date +%s)-$$"
WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/xianix-e2e.XXXXXX")"
NONCE="$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
GIT_DAEMON_PID=""
E2E_CONTAINERS=()   # containers we matched, for cleanup

cleanup() {
    [ -n "${GIT_DAEMON_PID}" ] && kill "${GIT_DAEMON_PID}" >/dev/null 2>&1 || true
    if [ "${KEEP_ARTIFACTS}" != "1" ]; then
        for cid in ${E2E_CONTAINERS[@]+"${E2E_CONTAINERS[@]}"}; do
            docker rm -f "${cid}" >/dev/null 2>&1 || true
        done
        # Workspace volumes are labelled with the fixture repo URL; remove any
        # volume created for this run's unique fixture paths.
        for repo in "gh" "ado"; do
            local_url="git://${FIXTURE_HOST}:${FIXTURE_PORT}/fixture-${RUN_ID}-${repo}.git"
            docker volume ls -q --filter "label=xianix.repository=${local_url}" \
                | xargs -r docker volume rm -f >/dev/null 2>&1 || true
        done
    fi
    rm -rf "${WORK_ROOT}"
}
trap cleanup EXIT

# Two bare fixture repos (one per simulated platform) so each Tier-2 test gets
# its own workspace volume and container — unique per run via RUN_ID, which is
# also how the harness identifies "its" containers among anything else the
# agent may be doing.
create_fixture_repo() {
    local suffix="$1" src="${WORK_ROOT}/src-$1"
    mkdir -p "${src}"
    git -C "${src}" init -q -b main
    cat > "${src}/README.md" <<'EOF'
# Xianix E2E Fixture

Tiny repository used by the TheAgent end-to-end tests.
EOF
    printf 'The e2e token for this run is: %s\n' "${NONCE}" > "${src}/TOKEN.txt"
    git -C "${src}" -c user.name=xianix-e2e -c user.email=e2e@xianix.test add -A
    git -C "${src}" -c user.name=xianix-e2e -c user.email=e2e@xianix.test commit -q -m "e2e fixture"
    git clone -q --bare "${src}" "${WORK_ROOT}/serve/fixture-${RUN_ID}-${suffix}.git"
}

start_git_daemon() {
    mkdir -p "${WORK_ROOT}/serve"
    create_fixture_repo "gh"
    create_fixture_repo "ado"
    git daemon --export-all --reuseaddr --listen=0.0.0.0 --port="${FIXTURE_PORT}" \
        --base-path="${WORK_ROOT}/serve" "${WORK_ROOT}/serve" \
        >/dev/null 2>&1 &
    GIT_DAEMON_PID=$!
    disown "${GIT_DAEMON_PID}" 2>/dev/null || true   # no job-control noise when the trap kills it
    sleep 1
    if ! kill -0 "${GIT_DAEMON_PID}" 2>/dev/null; then
        echo "ERROR: git daemon failed to start on port ${FIXTURE_PORT} (try FIXTURE_PORT=9419)." >&2
        exit 1
    fi
    # Sanity: clone from the host side through the daemon.
    if ! git clone -q "git://127.0.0.1:${FIXTURE_PORT}/fixture-${RUN_ID}-gh.git" "${WORK_ROOT}/probe" 2>/dev/null; then
        echo "ERROR: could not clone the fixture through git daemon — aborting." >&2
        exit 1
    fi
    rm -rf "${WORK_ROOT}/probe"
}

GH_REPO_URL="git://${FIXTURE_HOST}:${FIXTURE_PORT}/fixture-${RUN_ID}-gh.git"
ADO_REPO_URL="git://${FIXTURE_HOST}:${FIXTURE_PORT}/fixture-${RUN_ID}-ado.git"

# ── Webhook senders ───────────────────────────────────────────────────────────
LAST_HTTP_BODY=""
LAST_HTTP_CODE=""

send_webhook() {   # $1 = X-GitHub-Event value or "" for ADO-style, $2 = payload
    local event_header="$1" payload="$2" response
    local -a headers=(-H "Content-Type: application/json")
    [ -n "${event_header}" ] && headers+=(-H "X-GitHub-Event: ${event_header}")
    headers+=(-H "X-GitHub-Delivery: $(uuidgen 2>/dev/null || echo "e2e-${RANDOM}")")

    response="$(curl -sS -w $'\n%{http_code}' -X POST "${WEBHOOK_URL}" "${headers[@]}" -d "${payload}")" || {
        LAST_HTTP_BODY=""; LAST_HTTP_CODE="000"; return 1;
    }
    LAST_HTTP_CODE="${response##*$'\n'}"
    LAST_HTTP_BODY="${response%$'\n'*}"
}

# The agent's webhook handler answers {"status": "...", ...}; the platform may
# nest it, so search for the first status field anywhere in the body.
resp_status() {
    echo "${LAST_HTTP_BODY}" | jq -r '.. | objects | select(has("status")) | .status' 2>/dev/null | head -n1
}

github_pr_payload() {   # $1 = pr number, $2 = clone url, $3 = label
    jq -cn --argjson num "$1" --arg url "$2" --arg label "$3" '{
        action: "opened",
        number: $num,
        pull_request: {
            number: $num,
            title: "E2E simulated PR",
            state: "open",
            labels: (if $label == "" then [] else [{name: $label}] end),
            head: { ref: "main", sha: "0000000000000000000000000000000000000000" },
            base: { ref: "main" },
            user: { login: "e2e-bot" }
        },
        repository: {
            name: "e2e-fixture",
            full_name: "xianix-e2e/e2e-fixture",
            clone_url: $url,
            html_url: "https://example.invalid/e2e-fixture"
        },
        sender: { login: "e2e-bot" }
    }'
}

ado_pr_payload() {   # $1 = pr number, $2 = clone url, $3 = label
    jq -cn --argjson num "$1" --arg url "$2" --arg label "$3" '{
        subscriptionId: "00000000-0000-0000-0000-000000000000",
        notificationId: 1,
        id: "e2e-ado-notification",
        eventType: "git.pullrequest.created",
        publisherId: "tfs",
        message: { text: "E2E bot created a new pull request" },
        resource: {
            repository: {
                id: "e2e-repo-id",
                name: "e2e-fixture",
                remoteUrl: $url,
                project: { name: "E2E" }
            },
            pullRequestId: $num,
            status: "active",
            title: "E2E simulated PR",
            sourceRefName: "refs/heads/main",
            targetRefName: "refs/heads/main",
            labels: (if $label == "" then [] else [{name: $label, active: true}] end),
            createdBy: { displayName: "e2e-bot", uniqueName: "e2e@xianix.test" }
        },
        resourceVersion: "1.0",
        createdDate: "2026-01-01T00:00:00Z"
    }'
}

# ── Docker observation helpers ────────────────────────────────────────────────
managed_containers() { docker ps -aq --no-trunc --filter "label=xianix.managed=true"; }

# Finds the container whose XIANIX-INPUTS env references the given repo URL,
# excluding IDs listed in $2 (whitespace-separated snapshot taken pre-webhook).
find_container_for_repo() {
    local repo_url="$1" before="$2" cid
    for cid in $(managed_containers); do
        case " ${before} " in *" ${cid} "*) continue;; esac
        if docker inspect --format '{{join .Config.Env "\n"}}' "${cid}" 2>/dev/null \
            | grep -qF "${repo_url}"; then
            echo "${cid}"
            return 0
        fi
    done
    return 1
}

wait_for_container() {   # $1 = repo url, $2 = pre-webhook snapshot → echoes container id
    local deadline=$(( $(date +%s) + CONTAINER_START_TIMEOUT )) cid
    while [ "$(date +%s)" -lt "${deadline}" ]; do
        if cid="$(find_container_for_repo "$1" "$2")"; then
            echo "${cid}"
            return 0
        fi
        sleep 2
    done
    return 1
}

wait_for_exit() {   # $1 = container id → sets LAST_CONTAINER_EXIT
    local deadline=$(( $(date +%s) + CONTAINER_EXIT_TIMEOUT )) state
    while [ "$(date +%s)" -lt "${deadline}" ]; do
        state="$(docker inspect --format '{{.State.Status}}' "$1" 2>/dev/null || echo "gone")"
        if [ "${state}" = "exited" ]; then
            LAST_CONTAINER_EXIT="$(docker inspect --format '{{.State.ExitCode}}' "$1")"
            return 0
        fi
        [ "${state}" = "gone" ] && return 1
        sleep 5
    done
    return 1
}

# Asserts that NO new managed container referencing $1 appears within a short
# grace window (for the "ignored" routing tests).
assert_no_container() {
    local repo_url="$1" before="$2"
    sleep 8
    if find_container_for_repo "${repo_url}" "${before}" >/dev/null; then
        return 1
    fi
    return 0
}

# ── Full-chain runner (shared by the GitHub and ADO Tier-2 tests) ─────────────
# $5 = "dup" to immediately resend the identical payload and assert the 30s
#      dedup window suppresses it (must happen right after the first send,
#      before the window lapses during the Claude run).
run_full_chain_test() {
    local label="$1" repo_url="$2" payload="$3" event_header="$4" dup_check="${5:-}"
    local before cid status

    before="$(managed_containers | tr '\n' ' ')"

    if ! send_webhook "${event_header}" "${payload}"; then
        t_fail "${label}: webhook POST failed (is the ACP up at that URL?)"
        return
    fi
    status="$(resp_status)"
    if [ "${status}" = "success" ]; then
        t_pass "${label}: webhook response status=success"
    else
        t_fail "${label}: expected response status=success, got '${status}' (HTTP ${LAST_HTTP_CODE}): $(echo "${LAST_HTTP_BODY}" | head -c 300)"
        return
    fi

    if [ "${dup_check}" = "dup" ]; then
        if send_webhook "${event_header}" "${payload}"; then
            status="$(resp_status)"
            if [ "${status}" = "ignored" ]; then
                t_pass "${label}: identical redelivery suppressed by the dedup guard (status=ignored)"
            else
                t_fail "${label}: duplicate delivery answered '${status}', expected ignored"
            fi
        else
            t_fail "${label}: duplicate webhook POST failed"
        fi
    fi

    note "waiting up to ${CONTAINER_START_TIMEOUT}s for the executor container..."
    if cid="$(wait_for_container "${repo_url}" "${before}")"; then
        t_pass "${label}: executor container started ($(echo "${cid}" | cut -c1-12))"
        E2E_CONTAINERS+=("${cid}")
    else
        t_fail "${label}: no executor container appeared for ${repo_url}"
        return
    fi

    note "waiting up to ${CONTAINER_EXIT_TIMEOUT}s for it to finish (Claude run)..."
    if wait_for_exit "${cid}"; then
        if [ "${LAST_CONTAINER_EXIT}" -eq 0 ]; then
            t_pass "${label}: container exited 0"
        else
            t_fail "${label}: container exited ${LAST_CONTAINER_EXIT}"
            note "── last 40 lines of container stderr ──"
            docker logs "${cid}" 2>&1 >/dev/null | tail -n 40 | sed 's/^/  │ /'
            return
        fi
    else
        t_fail "${label}: container did not exit within ${CONTAINER_EXIT_TIMEOUT}s"
        return
    fi

    local stdout_file="${WORK_ROOT}/${label}-stdout" envelope result
    docker logs "${cid}" 2>"${WORK_ROOT}/${label}-stderr" >"${stdout_file}"
    envelope="$(jq -cs 'last' "${stdout_file}" 2>/dev/null || true)"
    if [ -n "${envelope}" ] && [ "${envelope}" != "null" ]; then
        t_pass "${label}: stdout carries a parseable JSON envelope"
    else
        t_fail "${label}: could not parse a JSON envelope from container stdout: $(head -c 200 "${stdout_file}")"
        return
    fi

    if [ "$(echo "${envelope}" | jq -r '.status')" = "completed" ]; then
        t_pass "${label}: envelope status=completed"
    else
        t_fail "${label}: envelope status=$(echo "${envelope}" | jq -r '.status'), error=$(echo "${envelope}" | jq -r '.error // "n/a"' | head -c 200)"
        return
    fi

    result="$(echo "${envelope}" | jq -r '.result // ""')"
    if echo "${result}" | grep -qF "${NONCE}"; then
        t_pass "${label}: Claude echoed the planted token from TOKEN.txt (full chain verified)"
        note "cost: \$$(echo "${envelope}" | jq -r '.cost_usd // "?"') · $(echo "${envelope}" | jq -r '.duration_seconds // "?"')s"
    else
        t_fail "${label}: planted token not found in Claude's result: $(echo "${result}" | head -c 200)"
    fi

    if docker volume ls -q --filter "label=xianix.repository=${repo_url}" | grep -q .; then
        t_pass "${label}: labelled workspace volume exists for the fixture repo"
    else
        t_fail "${label}: no workspace volume labelled xianix.repository=${repo_url}"
    fi
}

# ── Tier 1: routing (free) ────────────────────────────────────────────────────
test_unknown_event_ignored() {
    banner "Test 1: unrecognised event is answered 'ignored', no container starts"
    local before payload
    before="$(managed_containers | tr '\n' ' ')"
    payload='{"action":"e2e-noise","zen":"Non-blocking is better than blocking."}'

    if ! send_webhook "ping" "${payload}"; then
        t_fail "webhook POST failed (is the ACP up at ${WEBHOOK_URL}?)"
        return
    fi
    local status; status="$(resp_status)"
    if [ "${status}" = "ignored" ]; then
        t_pass "response status=ignored"
    else
        t_fail "expected status=ignored, got '${status}' (HTTP ${LAST_HTTP_CODE}): $(echo "${LAST_HTTP_BODY}" | head -c 300)"
    fi
    if assert_no_container "e2e-noise" "${before}"; then
        t_pass "no executor container was started"
    else
        t_fail "an executor container was started for a non-matching event"
    fi
}

test_unlabelled_pr_ignored() {
    banner "Test 2: PR opened WITHOUT the e2e/echo label is filtered out"
    local before payload
    before="$(managed_containers | tr '\n' ' ')"
    payload="$(github_pr_payload 9001 "${GH_REPO_URL}" "")"

    send_webhook "pull_request" "${payload}" || { t_fail "webhook POST failed"; return; }
    local status; status="$(resp_status)"
    if [ "${status}" = "ignored" ]; then
        t_pass "response status=ignored (match-any filter miss)"
    else
        t_fail "expected status=ignored, got '${status}': $(echo "${LAST_HTTP_BODY}" | head -c 300)"
    fi
    if assert_no_container "${GH_REPO_URL}" "${before}"; then
        t_pass "no executor container was started"
    else
        t_fail "an executor container was started despite the filter miss"
    fi
}

# ── Tier 2: full chain (GitHub + ADO shapes, ~$0.02 total) ────────────────────
test_github_full_chain() {
    banner "Test 3: GitHub PR-opened webhook → dedup guard → executor → Claude echoes fixture token"
    local pr_number=$(( (RANDOM % 9000) + 1000 ))
    local payload; payload="$(github_pr_payload "${pr_number}" "${GH_REPO_URL}" "e2e/echo")"
    run_full_chain_test "github" "${GH_REPO_URL}" "${payload}" "pull_request" "dup"
}

test_ado_full_chain() {
    banner "Test 4: Azure DevOps PR-created webhook → executor → Claude echoes fixture token"
    local pr_number=$(( (RANDOM % 9000) + 1000 ))
    local payload; payload="$(ado_pr_payload "${pr_number}" "${ADO_REPO_URL}" "e2e/echo")"
    run_full_chain_test "ado" "${ADO_REPO_URL}" "${payload}" ""
}

# ── Main ─────────────────────────────────────────────────────────────────────
echo "${C_BOLD}Xianix TheAgent — end-to-end tests${C_RESET}"
echo "  Webhook URL:  ${WEBHOOK_URL%%\?*}?…"
echo "  Fixture:      git://${FIXTURE_HOST}:${FIXTURE_PORT}/fixture-${RUN_ID}-{gh,ado}.git"
echo "  Run token:    ${NONCE}"

start_git_daemon

test_unknown_event_ignored
test_unlabelled_pr_ignored

if [ "${SKIP_TIER2}" = "1" ]; then
    banner "Tier 2 skipped (SKIP_TIER2=1)"
    t_skip "GitHub full-chain test"
    t_skip "Azure DevOps full-chain test"
else
    test_github_full_chain
    test_ado_full_chain
fi

banner "Summary"
if [ "${TESTS_FAILED}" -eq 0 ]; then
    echo "  ${C_GREEN}${C_BOLD}${TESTS_RUN} checks passed.${C_RESET}"
else
    echo "  ${C_RED}${C_BOLD}${TESTS_FAILED} of ${TESTS_RUN} checks failed.${C_RESET}"
    exit 1
fi

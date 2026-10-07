#!/usr/bin/env bash
# Integration tests for the Executor.OpenCode Docker image.
#
# WHAT THIS DOES
# --------------
# Treats the executor as a black box, exactly like the control plane does:
# environment variables go in, a JSON envelope comes out on stdout, progress
# logs come out on stderr, and the exit code signals success or failure.
#
# Instead of a real GitHub repository, the tests generate a small local git
# repository ("the fixture") on the fly and mount it into the container.
# The executor clones from that local path, which means:
#   * no network, no GitHub token, no rate limits (fully hermetic), and
#   * the fixture can contain a fresh random token each run, so the live
#     OpenCode test can only pass if the agent genuinely cloned and read the repo.
#
# TEST TIERS
# ----------
#   Tier 0 (always runs, free, no Docker):
#     0. host_context.py unit tests (platform preamble for PROMPT).
#
#   Tier 1 (always runs, free):
#     1–5. prepare / fetch / bad-URL / default-branch worktree.
#     6.   Runtime provisioner against repo version files (hermetic, --network none).
#     6b.  Repo global.json autodetection + XIANIX_RUNTIME_AUTODETECT=0.
#     6c.  On-demand runtime fallback hook (hermetic).
#
#   Tier R (needs XIANIX_IT_RUNTIMES=1; downloads a real .NET SDK, ~200MB):
#     7. Live dotnet provisioning onto a runtime volume; cache hit on second run.
#
#   Tier 2 (needs OPENAI_API_KEY or ANTHROPIC_API_KEY; skipped when absent):
#     8. Full prepare-and-execute where OpenCode must read a planted secret token.
#
# HOW TO RUN
# ----------
#   ./integration_test.sh                       # builds the image, runs everything
#   SKIP_BUILD=1 IMAGE=my-tag ./integration_test.sh   # reuse an already-built image
#   OPENAI_API_KEY=sk-... ./integration_test.sh       # include the live OpenCode test
#   SHOW_LOGS=0 ./integration_test.sh           # quiet: container logs only on failure
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXECUTOR_DIR="$(dirname "${SCRIPT_DIR}")"

# Local convenience: a gitignored tests/.env can hold API keys so the live
# OpenCode test runs without exporting anything. Already-exported keys win.
if [ -z "${OPENAI_API_KEY:-}" ] && [ -f "${SCRIPT_DIR}/.env" ]; then
    OPENAI_API_KEY="$(sed -n 's/^OPENAI_API_KEY=//p' "${SCRIPT_DIR}/.env" | head -n1)"
    [ -n "${OPENAI_API_KEY}" ] && echo "Loaded OPENAI_API_KEY from tests/.env"
fi
if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -f "${SCRIPT_DIR}/.env" ]; then
    ANTHROPIC_API_KEY="$(sed -n 's/^ANTHROPIC_API_KEY=//p' "${SCRIPT_DIR}/.env" | head -n1)"
    [ -n "${ANTHROPIC_API_KEY}" ] && echo "Loaded ANTHROPIC_API_KEY from tests/.env"
fi

IMAGE="${IMAGE:-executor.opencode:integration-test}"

# ── Pretty output helpers ─────────────────────────────────────────────────────

# SHOW_LOGS=1 (default) streams every container's log output live into the
# console, indented and dimmed so the PASS/FAIL lines stay easy to scan.
# Set SHOW_LOGS=0 for a quiet run where logs are only shown when a check fails.
SHOW_LOGS="${SHOW_LOGS:-1}"

# Colors only when writing to a terminal (and not opted out via NO_COLOR);
# CI logs and redirected output stay free of escape codes.
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'
    C_BOLD=$'\033[1m';   C_DIM=$'\033[2m';  C_RESET=$'\033[0m'
else
    C_GREEN=""; C_RED=""; C_YELLOW=""; C_BOLD=""; C_DIM=""; C_RESET=""
fi

TESTS_RUN=0
TESTS_FAILED=0

banner() { echo; echo "${C_BOLD}━━━ $* ━━━${C_RESET}"; }

t_pass() {
    TESTS_RUN=$((TESTS_RUN + 1))
    echo "  ${C_GREEN}✔ PASS${C_RESET}  $*"
}

t_skip() { echo "  ${C_YELLOW}– SKIP${C_RESET}  $*"; }

t_fail() {
    TESTS_RUN=$((TESTS_RUN + 1))
    TESTS_FAILED=$((TESTS_FAILED + 1))
    echo "  ${C_RED}✘ FAIL${C_RESET}  $*"
    # When logs were streamed live they are already on screen; otherwise show
    # the tail of the failed container's log so the failure is diagnosable.
    if [ "${SHOW_LOGS}" != "1" ] && [ -s "${STDERR_FILE}" ]; then
        echo "  ${C_DIM}── last 40 lines of container logs ──${C_RESET}"
        tail -n 40 "${STDERR_FILE}" | sed 's/^/  │ /'
        echo "  ${C_DIM}─────────────────────────────────────${C_RESET}"
    fi
}

# ── Workspace / fixture setup ─────────────────────────────────────────────────

WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/xianix-it.XXXXXX")"
STDOUT_FILE="${WORK_ROOT}/last-stdout"
STDERR_FILE="${WORK_ROOT}/last-stderr"

# Two docker volumes: the main one shared by the happy-path tests (so we can
# exercise the clone-then-reuse behaviour), and a separate one for the bad-URL
# test (a pre-populated volume would let the executor fall back to the already
# cloned repo and hide the failure we want to provoke).
VOLUME_MAIN="xianix-it-main-$$"
VOLUME_BADURL="xianix-it-badurl-$$"
VOLUME_RUNTIMES="xianix-it-runtimes-$$"

cleanup() {
    docker volume rm -f "${VOLUME_MAIN}" "${VOLUME_BADURL}" "${VOLUME_RUNTIMES}" >/dev/null 2>&1 || true
    rm -rf "${WORK_ROOT}"
}
trap cleanup EXIT

# The fixture: a normal repo with a couple of files, cloned to a bare repo the
# executor will use as its "remote". SECRET.md holds a random token generated
# fresh for every test run — the OpenCode test asserts this exact token comes
# back, which is impossible to fake without actually reading the repo.
NONCE="$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
FIXTURE_SRC="${WORK_ROOT}/fixture-src"
FIXTURE_MOUNT_DIR="${WORK_ROOT}/fixtures"
FIXTURE_BARE="${FIXTURE_MOUNT_DIR}/fixture.git"

# Path of the bare fixture as seen from inside the container.
REPO_URL_IN_CONTAINER="/fixtures/fixture.git"

# Global gitconfig handed to the container via GIT_CONFIG_GLOBAL. The fixture
# is owned by the host user while the container runs as `xianix`, and recent
# git refuses to read repos owned by someone else ("dubious ownership").
# Crucially, git honors safe.directory ONLY from system/global config FILES —
# GIT_CONFIG_COUNT/-c style config is deliberately ignored for this key — so
# mounting a config file is the only env-driven way to whitelist the fixture.
FIXTURE_GITCONFIG="${FIXTURE_MOUNT_DIR}/gitconfig"
GITCONFIG_IN_CONTAINER="/fixtures/gitconfig"

fixture_git() { git -C "${FIXTURE_SRC}" -c user.name=xianix-it -c user.email=it@xianix.test "$@"; }

create_fixture_repo() {
    mkdir -p "${FIXTURE_SRC}" "${FIXTURE_MOUNT_DIR}"
    git -C "${FIXTURE_SRC}" init -q -b main

    cat > "${FIXTURE_SRC}/README.md" <<'EOF'
# Executor.OpenCode Test Fixture

A tiny repository used by the Executor.OpenCode integration tests.
EOF
    cat > "${FIXTURE_SRC}/SECRET.md" <<EOF
The secret token for this test run is: ${NONCE}
EOF
    fixture_git add -A
    fixture_git commit -q -m "Initial fixture commit"

    # The bare copy is what the executor sees as its "remote". The source repo
    # keeps it as `origin` so later tests can push new commits to it.
    git clone -q --bare "${FIXTURE_SRC}" "${FIXTURE_BARE}"
    fixture_git remote add origin "${FIXTURE_BARE}"

    printf '[safe]\n\tdirectory = *\n' > "${FIXTURE_GITCONFIG}"
    chmod 444 "${FIXTURE_GITCONFIG}"
}

# ── Container runners ─────────────────────────────────────────────────────────

# Runs the executor image the same way the control plane does. Captures stdout
# (the JSON envelope) and stderr (the progress logs) into files, and the exit
# code into LAST_EXIT (never aborts the test suite itself). Extra `-e
# NAME=value` args are passed through. With SHOW_LOGS=1 the container's log
# stream is also echoed live, dimmed and gutter-prefixed.
#
# GIT_CONFIG_GLOBAL points git at the mounted gitconfig whose safe.directory=*
# marks all paths as safe (see FIXTURE_GITCONFIG above for why a config FILE is
# required — GIT_CONFIG_COUNT/-c are ignored for this key).
LAST_EXIT=0
LAST_DURATION=0
run_executor() {
    local volume="$1"
    shift
    : > "${STDOUT_FILE}"
    : > "${STDERR_FILE}"

    local docker_args=(
        --rm
        -v "${FIXTURE_MOUNT_DIR}:/fixtures:ro"
        -v "${volume}:/workspace/repo"
        -e "GIT_CONFIG_GLOBAL=${GITCONFIG_IN_CONTAINER}"
        "$@"
        "${IMAGE}"
    )

    local started
    started="$(date +%s)"
    set +e
    if [ "${SHOW_LOGS}" = "1" ]; then
        echo "  ${C_DIM}┌─ container logs ───────────────────────────────${C_RESET}"
        # `2>&1 >file` sends the log stream (stderr) into the pipe while the
        # JSON envelope (stdout) still lands in STDOUT_FILE untouched.
        docker run "${docker_args[@]}" 2>&1 >"${STDOUT_FILE}" \
            | tee "${STDERR_FILE}" \
            | while IFS= read -r line; do
                  printf '  %s│ %s%s\n' "${C_DIM}" "${line}" "${C_RESET}"
              done
        LAST_EXIT="${PIPESTATUS[0]}"
        LAST_DURATION=$(( $(date +%s) - started ))
        echo "  ${C_DIM}└─ exit code ${LAST_EXIT} after ${LAST_DURATION}s ─────────────────────${C_RESET}"
    else
        docker run "${docker_args[@]}" >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
        LAST_EXIT=$?
        LAST_DURATION=$(( $(date +%s) - started ))
    fi
    set -e
}

# Runs an arbitrary bash command inside the image with the volume mounted, so
# tests can inspect what the executor left on the volume.
inspect_volume() {
    local volume="$1" cmd="$2"
    docker run --rm --entrypoint bash \
        -v "${FIXTURE_MOUNT_DIR}:/fixtures:ro" \
        -v "${volume}:/workspace/repo" \
        -e "GIT_CONFIG_GLOBAL=${GITCONFIG_IN_CONTAINER}" \
        "${IMAGE}" -c "${cmd}"
}

# The XIANIX-INPUTS payload for the fixture. platform=local is deliberate:
# the executor requires a real token for github/azuredevops, but an unknown
# platform just logs a warning and clones without credentials — which is
# exactly right for a local-path fixture.
XIANIX_INPUTS_JSON="$(jq -cn --arg url "${REPO_URL_IN_CONTAINER}" \
    '{"repository-url": $url, "platform": "local"}')"

# ── Tier 1: hermetic pipeline tests (no API key needed) ───────────────────────

test_prepare_clones_repo() {
    banner "Test 1: 'prepare' mode clones the repo onto the volume"
    run_executor "${VOLUME_MAIN}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-prepare-1" \
        -e "XIANIX-MODE=prepare" \
        -e "XIANIX-INPUTS=${XIANIX_INPUTS_JSON}"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    # Success in prepare mode must print NOTHING to stdout — stdout belongs to
    # the JSON result envelope, and the control plane parses it as such.
    if [ ! -s "${STDOUT_FILE}" ]; then
        t_pass "stdout is empty on success (envelope contract)"
    else
        t_fail "stdout was not empty: $(head -c 200 "${STDOUT_FILE}")"
    fi

    if inspect_volume "${VOLUME_MAIN}" 'test -f /workspace/repo/HEAD' >/dev/null 2>&1; then
        t_pass "bare clone exists on the volume"
    else
        t_fail "no bare clone found on the volume (missing /workspace/repo/HEAD)"
    fi
}

test_prepare_reuses_clone() {
    banner "Test 2: a second 'prepare' run reuses the clone (fetch, not re-clone)"
    run_executor "${VOLUME_MAIN}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-prepare-2" \
        -e "XIANIX-MODE=prepare" \
        -e "XIANIX-INPUTS=${XIANIX_INPUTS_JSON}"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    if grep -q "Fetching into existing repo" "${STDERR_FILE}"; then
        t_pass "took the fetch path instead of re-cloning"
    else
        t_fail "expected 'Fetching into existing repo' in the logs"
    fi
}

test_new_commit_is_picked_up() {
    banner "Test 3: a new commit in the fixture is picked up on the next run"
    echo "An update made at $(date -u)" > "${FIXTURE_SRC}/UPDATE.md"
    fixture_git add -A
    fixture_git commit -q -m "Second fixture commit"
    fixture_git push -q origin main
    local new_sha
    new_sha="$(fixture_git rev-parse main)"

    run_executor "${VOLUME_MAIN}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-prepare-3" \
        -e "XIANIX-MODE=prepare" \
        -e "XIANIX-INPUTS=${XIANIX_INPUTS_JSON}"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    local volume_sha
    volume_sha="$(inspect_volume "${VOLUME_MAIN}" 'git -C /workspace/repo rev-parse HEAD' 2>/dev/null || echo '?')"
    if [ "${volume_sha}" = "${new_sha}" ]; then
        t_pass "volume clone HEAD matches the new commit (${new_sha:0:12})"
    else
        t_fail "volume HEAD is '${volume_sha}', expected '${new_sha}' (default-branch refresh broken?)"
    fi
}

test_bad_url_error_envelope() {
    banner "Test 4: a bad repository URL produces the structured error envelope"
    local bad_inputs
    bad_inputs="$(jq -cn '{"repository-url": "/fixtures/does-not-exist.git", "platform": "local"}')"

    run_executor "${VOLUME_BADURL}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-bad-url" \
        -e "XIANIX-MODE=prepare" \
        -e "XIANIX-INPUTS=${bad_inputs}"

    if [ "${LAST_EXIT}" -ne 0 ]; then
        t_pass "container exited non-zero (${LAST_EXIT})"
    else
        t_fail "container exited 0 for a nonexistent repository"
        return
    fi

    # The control plane parses ONE envelope shape for every failure mode, so a
    # prepare-phase crash must still emit valid JSON with phase + status set.
    if jq -e '.phase == "prepare" and .status == "error"' "${STDOUT_FILE}" >/dev/null 2>&1; then
        t_pass "stdout carries the prepare error envelope (phase=prepare, status=error)"
    else
        t_fail "stdout is not the expected error envelope: $(head -c 300 "${STDOUT_FILE}")"
    fi
}

test_worktree_uses_default_branch() {
    banner "Test 5: worktree is created from the default branch (HEAD)"

    # Every run starts on the default branch; plugins resolve/fetch any
    # task-specific refs from the prompt themselves. The executor never sees
    # the PR number as a checkout target.
    local main_sha
    main_sha="$(fixture_git rev-parse main)"

    local inputs
    inputs="$(jq -cn --arg url "${REPO_URL_IN_CONTAINER}" \
        '{"repository-url": $url, "platform": "local", "pr-number": "9"}')"

    # Prepare script only — avoids chaining into run_prompt.sh (needs API key).
    run_executor "${VOLUME_MAIN}" \
        --entrypoint /workspace/prepare_repo.sh \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-default-head" \
        -e "XIANIX-MODE=prepare-and-execute" \
        -e "XIANIX-INPUTS=${inputs}"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    if grep -q "Creating worktree for HEAD" "${STDERR_FILE}"; then
        t_pass "worktree was created from HEAD (default branch)"
    else
        t_fail "expected a 'Creating worktree for HEAD' log line"
    fi

    local volume_sha
    volume_sha="$(inspect_volume "${VOLUME_MAIN}" 'git -C /workspace/repo rev-parse HEAD' 2>/dev/null || echo '?')"
    if [ "${volume_sha}" = "${main_sha}" ]; then
        t_pass "bare-clone HEAD matches the fixture's default branch (${main_sha:0:12})"
    else
        t_fail "bare-clone HEAD is '${volume_sha}', expected '${main_sha}'"
    fi
}

# ── Tier 1 (cont.): runtime provisioner — hermetic, no network ────────────────

# OpenCode provisions from repository version files only (no Claude plugin
# manifests). Fixture worktree carries .tool-versions + global.json.
create_runtime_fixture() {
    mkdir -p "${FIXTURE_MOUNT_DIR}/fake-worktree"
    cat > "${FIXTURE_MOUNT_DIR}/fake-worktree/.tool-versions" <<'EOF'
dotnet      9.0
node        22.11.0
EOF
    echo '{ "sdk": { "version": "9.0.100" } }' \
        > "${FIXTURE_MOUNT_DIR}/fake-worktree/global.json"
}

# Runs provision_runtimes.sh in the image with the fixture mounted and no
# network, so mise can collect declarations but never downloads.
run_provisioner_offline() {
    local extra_args="$1"
    : > "${STDOUT_FILE}"; : > "${STDERR_FILE}"
    set +e
    # shellcheck disable=SC2086  # extra_args is a deliberate arg list
    docker run --rm --network none \
        -v "${FIXTURE_MOUNT_DIR}:/fixtures:ro" \
        -v "${VOLUME_RUNTIMES}:/workspace/runtimes" \
        ${extra_args} \
        --entrypoint bash "${IMAGE}" -c '
            if [ "${SETUP_WORKTREE:-0}" = "1" ]; then
                mkdir -p /tmp/worktree
                cp -r /fixtures/fake-worktree/. /tmp/worktree/
            fi
            /workspace/provision_runtimes.sh /tmp/runtime-env.sh
            rc=$?
            echo "── env file ──"
            cat /tmp/runtime-env.sh 2>/dev/null
            exit "${rc}"
        ' >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
    LAST_EXIT=$?
    set -e
}

test_runtime_provisioner_hermetic() {
    banner "Test 6: runtime provisioner — repo declarations + failure isolation (hermetic)"
    create_runtime_fixture

    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e XIANIX_RUNTIME_AUTODETECT=1 \
        -e SETUP_WORKTREE=1 -e EXECUTION_ID=it-rt-6"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "provisioner exits 0 even when installs can't reach the network"
    else
        t_fail "provisioner exited ${LAST_EXIT}, expected 0 (it must never fail a run)"
        sed 's/^/  │ /' "${STDERR_FILE}" | tail -n 30
        return
    fi

    if grep -q 'Repository runtime declarations:.*\.tool-versions' "${STDERR_FILE}" \
        || grep -q 'Repository runtime declarations:.*global.json' "${STDERR_FILE}"; then
        t_pass "repo version files were detected"
    else
        t_fail "expected .tool-versions and/or global.json in repository declarations"
    fi

    if grep -q "Runtime cache root: /workspace/runtimes" "${STDERR_FILE}"; then
        t_pass "installs are targeted at the mounted runtime volume"
    else
        t_fail "provisioner did not resolve the mounted runtime volume as its cache root"
    fi
}

test_runtime_autodetects_repo_version_file() {
    banner "Test 6b: runtime provisioner detects the repo's own version files"

    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e SETUP_WORKTREE=1 \
        -e EXECUTION_ID=it-rt-6b"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "provisioner exits 0"
    else
        t_fail "provisioner exited ${LAST_EXIT}"
        return
    fi

    if grep -q 'Repository runtime declarations:.*global.json' "${STDERR_FILE}"; then
        t_pass "repo global.json was picked up without any Xianix-specific manifest"
    else
        t_fail "expected global.json in the repository declarations"
    fi

    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e SETUP_WORKTREE=1 \
        -e EXECUTION_ID=it-rt-6b-off -e XIANIX_RUNTIME_AUTODETECT=0"

    if grep -q "No repository version file" "${STDERR_FILE}"; then
        t_pass "XIANIX_RUNTIME_AUTODETECT=0 ignores the repo's version files"
    else
        t_fail "expected no declarations to be found with autodetect disabled"
    fi
}

test_runtime_fallback_hook_hermetic() {
    banner "Test 6c: on-demand runtime fallback hook (hermetic)"

    local run_cmd='
        export EXECUTION_ID=it-rt-6c
        /workspace/provision_runtimes.sh /tmp/runtime-env.sh
        # shellcheck disable=SC1091
        set -a; . /tmp/runtime-env.sh; set +a
        echo "bash-env: ${BASH_ENV:-<unset>}"
        [ -f "${BASH_ENV:-/nonexistent}" ] && echo "hook-file: present"
        bash -c "terraform version" 2>&1 | sed "s/^/deny-terraform: /"
        bash -c "nosuchcmd" 2>&1        | sed "s/^/deny-typo: /"
        bash -c "nosuchcmd"; echo "deny-exit: $?"
        bash -c "node --version" 2>&1   | sed "s/^/passthrough-node: /"
        bash -c "cargo --version" 2>&1 | grep -oE "resolving [a-z@. ]+" \
            | sed "s/^/alias-cargo: /"
        bash -c "mvn --version" 2>&1 | grep -oE "resolving [a-z@. ]+" \
            | sed "s/^/alias-mvn: /"
        # shellcheck disable=SC1090
        . "${BASH_ENV}"
        sort -u "${XIANIX_FALLBACK_LOG}" | sed "s/^/ledger: /" | tr "\t" " "
        : > "${XIANIX_FALLBACK_LOG}"

        mkdir -p "${MISE_DATA_DIR}/installs/faketool/1.2.3"
        ln -sfn ./1.2.3 "${MISE_DATA_DIR}/installs/faketool/latest"
        xianix_record_runtime faketool 0
        mkdir -p "${MISE_DATA_DIR}/installs/fakenew/4.5.6"
        ln -sfn ./4.5.6 "${MISE_DATA_DIR}/installs/fakenew/latest"
        xianix_record_runtime fakenew 1
        xianix_record_runtime fakegone 1
        ls /workspace/runtimes/.meta | sed "s/^/meta: /"

        log() { echo "$*" >&2; }
        eval "$(awk "/^summarize_runtimes\(\) \{/,/^}/" /workspace/run_prompt.sh)"
        summarize_runtimes 2>&1 | sed "s/^/summary: /"
        exit 0
    '

    : > "${STDOUT_FILE}"; : > "${STDERR_FILE}"
    set +e
    docker run --rm --network none \
        -v "${VOLUME_RUNTIMES}:/workspace/runtimes" \
        --entrypoint bash "${IMAGE}" -c "${run_cmd}" >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
    LAST_EXIT=$?
    set -e

    if grep -q '^bash-env: /tmp/xianix-runtime-hook' "${STDOUT_FILE}" \
        && grep -q '^hook-file: present' "${STDOUT_FILE}"; then
        t_pass "BASH_ENV hook is emitted even when nothing is declared"
    else
        t_fail "expected a BASH_ENV hook file: $(tail -n 5 "${STDERR_FILE}" | tr '\n' ' ')"
        return
    fi

    if grep -q '^deny-terraform: bash: terraform: command not found' "${STDOUT_FILE}"; then
        t_pass "non-allow-listed binary is refused, not installed"
    else
        t_fail "expected the hook to refuse 'terraform'"
    fi

    if grep -q '^deny-typo: bash: nosuchcmd: command not found' "${STDOUT_FILE}" \
        && grep -q '^deny-exit: 127' "${STDOUT_FILE}"; then
        t_pass "an unknown command still fails with the conventional 127"
    else
        t_fail "expected exit 127 and the standard message for an unknown command"
    fi

    if grep -qE '^passthrough-node: v[0-9]+' "${STDOUT_FILE}"; then
        t_pass "a binary already on PATH is untouched by the hook"
    else
        t_fail "the hook interfered with the image's own node"
    fi

    if grep -q '^alias-cargo: resolving rust@latest' "${STDOUT_FILE}"; then
        t_pass "binary-to-tool alias maps 'cargo' onto the rust runtime"
    else
        t_fail "expected 'cargo' to resolve to rust@latest"
    fi

    if grep -q '^alias-mvn: resolving java@latest maven@latest' "${STDOUT_FILE}"; then
        t_pass "'mvn' co-installs the JDK alongside maven"
    else
        t_fail "expected 'mvn' to resolve to java@latest + maven@latest"
    fi

    if grep -qx 'meta: faketool@1.2.3.last-used' "${STDOUT_FILE}"; then
        t_pass "hook records runtime use so maintenance won't prune it while active"
    else
        t_fail "expected a faketool@1.2.3 last-used marker: $(grep '^meta:' "${STDOUT_FILE}" | tr '\n' ' ')"
    fi

    if grep -q '^ledger: java latest failed' "${STDOUT_FILE}" \
        && grep -q '^ledger: maven latest failed' "${STDOUT_FILE}"; then
        t_pass "a failed on-demand install is recorded, not swallowed"
    else
        t_fail "expected failed ledger rows for the offline mvn attempt: $(grep '^ledger:' "${STDOUT_FILE}" | tr '\n' ' ')"
    fi

    if grep -qE '^summary: \[runtimes\] Fetched on demand: fakenew 4\.5\.6 \([0-9]' "${STDOUT_FILE}"; then
        t_pass "summary reports a fresh download with its on-disk size"
    else
        t_fail "expected a 'Fetched on demand' line for fakenew: $(grep '^summary:' "${STDOUT_FILE}" | tr '\n' ' ')"
    fi

    if grep -qxF 'summary: [runtimes] Reused from cache: faketool 1.2.3' "${STDOUT_FILE}"; then
        t_pass "summary distinguishes a cache hit from a download"
    else
        t_fail "expected a 'Reused from cache' line for faketool"
    fi

    if grep -qxF 'summary: [runtimes] WARNING: on-demand install FAILED: fakegone@latest' "${STDOUT_FILE}"; then
        t_pass "summary warns about a runtime that never installed"
    else
        t_fail "expected a FAILED summary line for fakegone"
    fi

    : > "${STDOUT_FILE}"
    set +e
    docker run --rm --network none \
        -e XIANIX_RUNTIME_FALLBACK=0 \
        -e EXECUTION_ID=it-rt-6c-off \
        -v "${VOLUME_RUNTIMES}:/workspace/runtimes" \
        --entrypoint bash "${IMAGE}" -c '
            /workspace/provision_runtimes.sh /tmp/runtime-env.sh
            grep -c BASH_ENV /tmp/runtime-env.sh || true
        ' >"${STDOUT_FILE}" 2>/dev/null
    set -e

    if grep -qx '0' "${STDOUT_FILE}"; then
        t_pass "XIANIX_RUNTIME_FALLBACK=0 emits no hook"
    else
        t_fail "expected no BASH_ENV export with the fallback disabled"
    fi
}

# ── Tier R: live dotnet provisioning (needs network, ~200MB download) ─────────

test_runtime_dotnet_live() {
    banner "Test 7: live dotnet provisioning + cache reuse (XIANIX_IT_RUNTIMES=1)"

    if [ "${XIANIX_IT_RUNTIMES:-0}" != "1" ]; then
        t_skip "XIANIX_IT_RUNTIMES is not set — skipping the live .NET SDK download test."
        return
    fi

    local live_volume="xianix-it-rt-live-$$"
    local run_cmd='
        set -e
        export EXECUTION_ID=it-rt-live
        mkdir -p /tmp/live-worktree
        printf "dotnet 9.0\n" > /tmp/live-worktree/.tool-versions
        export WORK_DIR=/tmp/live-worktree
        /workspace/provision_runtimes.sh /tmp/runtime-env.sh
        source /tmp/runtime-env.sh
        echo "list-sdks: $(dotnet --list-sdks)"
        echo "version: $(dotnet --version)"
        echo "nuget-packages: ${NUGET_PACKAGES:-<unset>}"
    '

    : > "${STDERR_FILE}"
    set +e
    docker run --rm \
        -v "${live_volume}:/workspace/runtimes" \
        --entrypoint bash "${IMAGE}" -c "${run_cmd}" >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
    LAST_EXIT=$?
    set -e

    if [ "${LAST_EXIT}" -eq 0 ] && grep -qE '^list-sdks: 9\.[0-9]+' "${STDOUT_FILE}"; then
        t_pass "first run installed a .NET 9 SDK onto the volume"
    else
        t_fail "first live run failed (exit ${LAST_EXIT}): $(tail -n 5 "${STDERR_FILE}" | tr '\n' ' ')"
        docker volume rm -f "${live_volume}" >/dev/null 2>&1 || true
        return
    fi

    if grep -qE '^version: 9\.[0-9]+' "${STDOUT_FILE}"; then
        t_pass "the managed dotnet CLI starts (image has the SDK's runtime deps)"
    else
        t_fail "'dotnet --version' did not run — the image is missing a .NET runtime dependency: $(grep -A3 '^version:' "${STDOUT_FILE}" | tr '\n' ' ')"
    fi

    if grep -q '^nuget-packages: /workspace/runtimes/cache/nuget' "${STDOUT_FILE}"; then
        t_pass "NUGET_PACKAGES points at the persistent cache on the volume"
    else
        t_fail "NUGET_PACKAGES was not exported onto the runtime volume"
    fi

    : > "${STDERR_FILE}"
    set +e
    docker run --rm \
        -v "${live_volume}:/workspace/runtimes" \
        --entrypoint bash "${IMAGE}" -c "${run_cmd}" >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
    LAST_EXIT=$?
    set -e

    if [ "${LAST_EXIT}" -eq 0 ] \
        && grep -q "All declared runtimes already provisioned (cache hit)" "${STDERR_FILE}"; then
        t_pass "second run on the same volume hit the cache (no re-download)"
    else
        t_fail "second live run did not hit the cache (exit ${LAST_EXIT})"
    fi

    : > "${STDERR_FILE}"
    set +e
    docker run --rm \
        -v "${live_volume}:/workspace/runtimes" \
        -e WORK_DIR=/tmp/worktree \
        -e EXECUTION_ID=it-rt-live-gj \
        --entrypoint bash "${IMAGE}" -c '
            mkdir -p /tmp/worktree
            echo "{ \"sdk\": { \"version\": \"9.0.100\" } }" > /tmp/worktree/global.json
            /workspace/provision_runtimes.sh /tmp/runtime-env.sh
            source /tmp/runtime-env.sh
            echo "version: $(dotnet --version)"
        ' >"${STDOUT_FILE}" 2>"${STDERR_FILE}"
    LAST_EXIT=$?
    set -e

    if [ "${LAST_EXIT}" -eq 0 ] \
        && grep -q 'Repository runtime declarations: global.json' "${STDERR_FILE}" \
        && grep -qE '^version: 9\.[0-9]+' "${STDOUT_FILE}"; then
        t_pass "a repo global.json alone provisions the SDK (no plugin manifest needed)"
    else
        t_fail "global.json autodetection did not yield a working dotnet (exit ${LAST_EXIT})"
    fi

    docker volume rm -f "${live_volume}" >/dev/null 2>&1 || true
}

# ── Tier 2: real OpenCode run (needs OPENAI_API_KEY or ANTHROPIC_API_KEY) ─────

test_opencode_reads_planted_secret() {
    banner "Test 8: OpenCode reads the planted secret token from the repo"

    local api_key_env="" model=""
    if [ -n "${OPENAI_API_KEY:-}" ]; then
        api_key_env="OPENAI-API-KEY=${OPENAI_API_KEY}"
        model="${XIANIX_IT_MODEL:-openai/gpt-4.1-mini}"
    elif [ -n "${ANTHROPIC_API_KEY:-}" ]; then
        api_key_env="ANTHROPIC-API-KEY=${ANTHROPIC_API_KEY}"
        model="${XIANIX_IT_MODEL:-anthropic/claude-haiku-4-5}"
    else
        t_skip "OPENAI_API_KEY / ANTHROPIC_API_KEY is not set — skipping the live OpenCode test."
        return
    fi

    local prompt="Read the file SECRET.md at the root of this repository. It contains a secret token. Reply with the exact token and nothing else."

    run_executor "${VOLUME_MAIN}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-opencode-1" \
        -e "XIANIX-INPUTS=${XIANIX_INPUTS_JSON}" \
        -e "CLAUDE-CODE-PLUGINS=[]" \
        -e "PROMPT=${prompt}" \
        -e "${api_key_env}" \
        -e "XIANIX-MODEL=${model}"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    local line_count
    line_count="$(grep -c . "${STDOUT_FILE}" || true)"
    if [ "${line_count}" = "1" ] && jq -e . "${STDOUT_FILE}" >/dev/null 2>&1; then
        t_pass "stdout is exactly one line of valid JSON"
    else
        t_fail "stdout is polluted (${line_count} non-empty lines): $(head -c 300 "${STDOUT_FILE}")"
        return
    fi

    if jq -e '.status == "completed"' "${STDOUT_FILE}" >/dev/null; then
        t_pass "envelope status is 'completed'"
    else
        t_fail "envelope status is '$(jq -r '.status' "${STDOUT_FILE}")', error: $(jq -r '.error // ""' "${STDOUT_FILE}")"
        return
    fi

    if jq -e --arg nonce "${NONCE}" '(.result // "") | contains($nonce)' "${STDOUT_FILE}" >/dev/null; then
        t_pass "result contains the planted secret token — the agent really read the repo"
    else
        t_fail "result does not contain the token '${NONCE}': $(jq -r '.result' "${STDOUT_FILE}" | head -c 300)"
    fi

    echo
    echo "  Result envelope:"
    jq -r '
        "    agent reply : \(.result // "<none>" | gsub("\n"; " ") | .[0:120])",
        "    model(s)    : \(.models // [] | join(", "))",
        "    status      : \(.status // "<none>")",
        "    session     : \(.session_id // "<none>")"
    ' "${STDOUT_FILE}"
}

# ── Main ──────────────────────────────────────────────────────────────────────

for tool in docker git jq python3; do
    command -v "${tool}" >/dev/null || { echo "FATAL: '${tool}' is required." >&2; exit 1; }
done

banner "Unit: host_context preamble"
if python3 "${SCRIPT_DIR}/test_host_context_open.py"; then
    t_pass "host_context.py unit tests"
else
    t_fail "host_context.py unit tests"
fi

if [ -z "${SKIP_BUILD:-}" ]; then
    banner "Building Executor.OpenCode image (${IMAGE})"
    # Dockerfile COPY vendor/plugins — ensure a minimal stub when none published.
    mkdir -p "${EXECUTOR_DIR}/vendor/plugins/_it-stub/.claude-plugin"
    if [ ! -f "${EXECUTOR_DIR}/vendor/plugins/_it-stub/.claude-plugin/plugin.json" ]; then
        printf '%s\n' '{"name":"_it-stub","version":"0.0.0"}' \
            > "${EXECUTOR_DIR}/vendor/plugins/_it-stub/.claude-plugin/plugin.json"
    fi
    docker build -t "${IMAGE}" "${EXECUTOR_DIR}"
else
    banner "Using existing image (${IMAGE})"
fi

banner "Creating fixture repository (secret token: ${NONCE})"
create_fixture_repo

test_prepare_clones_repo
test_prepare_reuses_clone
test_new_commit_is_picked_up
test_bad_url_error_envelope
test_worktree_uses_default_branch
test_runtime_provisioner_hermetic
test_runtime_autodetects_repo_version_file
test_runtime_fallback_hook_hermetic
test_runtime_dotnet_live
test_opencode_reads_planted_secret

banner "Results"
if [ "${TESTS_FAILED}" -eq 0 ]; then
    echo "  ${C_GREEN}${C_BOLD}${TESTS_RUN} checks run, all passed.${C_RESET}"
else
    echo "  ${C_RED}${C_BOLD}${TESTS_RUN} checks run, ${TESTS_FAILED} failed.${C_RESET}"
fi
[ "${TESTS_FAILED}" -eq 0 ]

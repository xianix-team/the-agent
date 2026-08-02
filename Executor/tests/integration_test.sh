#!/usr/bin/env bash
# Integration tests for the Xianix Executor Docker image.
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
#   * the fixture can contain a fresh random token each run, so the Claude
#     test can only pass if the agent genuinely cloned and read the repo.
#
# TEST TIERS
# ----------
#   Tier 0 (always runs, free, no Docker):
#     0. host_context.py unit tests (platform preamble for PROMPT).
#
#   Tier 1 (always runs, free):
#     1. "prepare" mode clones the repo onto the volume.
#     2. A second "prepare" run reuses the existing clone (fetch, not re-clone).
#     3. A new commit pushed to the fixture is picked up on the next run.
#     4. A bad repository URL produces the structured prepare error envelope.
#     5. Worktree is created from the default branch (HEAD); plugins resolve
#        any task-specific refs from the prompt themselves.
#     6. Runtime provisioner: plugin `.tool-versions` entries are collected,
#        non-allow-listed tools / backend-prefixed names / traversal versions
#        are rejected, and an install that cannot reach the network is warned
#        about rather than failing the run. Runs with --network none, so it is
#        fully hermetic and downloads nothing.
#     6b. The repo's own version files (global.json here) are detected without
#        any Xianix-specific manifest, and XIANIX_RUNTIME_AUTODETECT=0 turns
#        that off.
#
#   Tier R (needs XIANIX_IT_RUNTIMES=1; downloads a real .NET SDK, ~200MB):
#     7. Live dotnet provisioning onto a runtime volume; a second run on the
#        same volume must hit the cache instead of re-downloading.
#
#   Tier 2 (needs ANTHROPIC_API_KEY, costs ~$0.01, skipped when the key is absent):
#     8. A full prepare-and-execute run where Claude Code must read a planted
#        secret token from the repo and return it in the result envelope.
#
# HOW TO RUN
# ----------
#   ./integration_test.sh                       # builds the image, runs everything
#   SKIP_BUILD=1 IMAGE=my-tag ./integration_test.sh   # reuse an already-built image
#   ANTHROPIC_API_KEY=sk-ant-... ./integration_test.sh  # include the Claude test
#   SHOW_LOGS=0 ./integration_test.sh           # quiet: container logs only on failure
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXECUTOR_DIR="$(dirname "${SCRIPT_DIR}")"

# Local convenience: a gitignored tests/.env can hold ANTHROPIC_API_KEY so the
# live Claude test runs without exporting anything. An already-exported key
# (e.g. from CI secrets) always wins over the file.
if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -f "${SCRIPT_DIR}/.env" ]; then
    ANTHROPIC_API_KEY="$(sed -n 's/^ANTHROPIC_API_KEY=//p' "${SCRIPT_DIR}/.env" | head -n1)"
    [ -n "${ANTHROPIC_API_KEY}" ] && echo "Loaded ANTHROPIC_API_KEY from tests/.env"
fi

IMAGE="${IMAGE:-xianix-executor:integration-test}"

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
# fresh for every test run — the Claude test asserts this exact token comes
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
# Executor Test Fixture

A tiny repository used by the Xianix Executor integration tests.
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

# Writes a fixture "installed plugin" (a .tool-versions manifest) plus a fake
# `claude` CLI shim that reports it, so provision_runtimes.sh can be exercised
# without installing real plugins. Also a fake worktree carrying a repo-level
# global.json, to exercise the autodetect path.
create_runtime_fixture() {
    local plugin_dir="${FIXTURE_MOUNT_DIR}/fake-plugin"
    mkdir -p "${plugin_dir}"
    # Two good entries plus three that must be rejected: a tool outside the
    # allow-list, mise's explicit backend syntax, and a traversal version.
    cat > "${plugin_dir}/.tool-versions" <<'EOF'
dotnet      9.0
node        22.11.0   # comments are allowed
terraform   1.9
cargo:evil  1.0
python      ../evil
EOF

    cat > "${FIXTURE_MOUNT_DIR}/claude-shim.sh" <<'EOF'
#!/usr/bin/env bash
# Minimal stand-in for the Claude CLI: only `plugin list --json` is used by
# provision_runtimes.sh.
if [ "${1:-}" = "plugin" ] && [ "${2:-}" = "list" ]; then
    echo '[{"id":"fake-plugin@test","installPath":"/fixtures/fake-plugin"}]'
fi
exit 0
EOF
    chmod +x "${FIXTURE_MOUNT_DIR}/claude-shim.sh"

    mkdir -p "${FIXTURE_MOUNT_DIR}/fake-worktree"
    echo '{ "sdk": { "version": "9.0.100" } }' \
        > "${FIXTURE_MOUNT_DIR}/fake-worktree/global.json"
}

# Runs provision_runtimes.sh in the image with the fixture mounted and no
# network, so mise can collect and validate declarations but never downloads.
# $1 is extra `docker run` args (as a single string), $2 the shell prelude.
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
            mkdir -p /tmp/bin
            if [ "${NO_PLUGINS:-0}" = "1" ]; then
                printf "#!/usr/bin/env bash\nexit 0\n" > /tmp/bin/claude
            else
                cp /fixtures/claude-shim.sh /tmp/bin/claude
            fi
            chmod +x /tmp/bin/claude
            export PATH="/tmp/bin:${PATH}"
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
    banner "Test 6: runtime provisioner — declarations, validation, failure isolation (hermetic)"
    create_runtime_fixture

    # Copy the fixture worktree somewhere writable inside the container: mise
    # resolves from the worktree, and /fixtures is mounted read-only.
    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e XIANIX_RUNTIME_AUTODETECT=1 \
        -e SETUP_WORKTREE=1"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "provisioner exits 0 even when installs can't reach the network"
    else
        t_fail "provisioner exited ${LAST_EXIT}, expected 0 (it must never fail a run)"
        sed 's/^/  │ /' "${STDERR_FILE}" | tail -n 30
        return
    fi

    if grep -q 'Plugin-declared runtimes:.*dotnet@9\.0' "${STDERR_FILE}" \
        && grep -q 'Plugin-declared runtimes:.*node@22\.11\.0' "${STDERR_FILE}"; then
        t_pass "valid .tool-versions entries were collected"
    else
        t_fail "expected dotnet@9.0 and node@22.11.0 in the plugin-declared list"
    fi

    if grep -q "runtime 'terraform' is not allow-listed" "${STDERR_FILE}"; then
        t_pass "non-allow-listed runtime was rejected"
    else
        t_fail "expected an allow-list warning for 'terraform'"
    fi

    if grep -q "runtime 'cargo:evil' is not allow-listed" "${STDERR_FILE}"; then
        t_pass "explicit backend syntax was rejected"
    else
        t_fail "expected 'cargo:evil' to be rejected"
    fi

    if grep -q "invalid version '../evil'" "${STDERR_FILE}"; then
        t_pass "path-traversal version string was rejected"
    else
        t_fail "expected a warning for the invalid '../evil' version"
    fi

    if grep -q "Runtime cache root: /workspace/runtimes" "${STDERR_FILE}"; then
        t_pass "installs are targeted at the mounted runtime volume"
    else
        t_fail "provisioner did not resolve the mounted runtime volume as its cache root"
    fi
}

test_runtime_autodetects_repo_version_file() {
    banner "Test 6b: runtime provisioner detects the repo's own version files"

    # No plugins this time — the only declaration is the worktree's global.json.
    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e SETUP_WORKTREE=1 -e NO_PLUGINS=1"

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

    # Autodetection off must ignore it and, with no plugins, find nothing at all.
    run_provisioner_offline "-e WORK_DIR=/tmp/worktree -e SETUP_WORKTREE=1 -e NO_PLUGINS=1 \
        -e XIANIX_RUNTIME_AUTODETECT=0"

    # Wording differs depending on whether the on-demand fallback is armed; the
    # invariant under test is that nothing was *declared*, so match the prefix.
    if grep -q "No plugin manifest and no repository version file" "${STDERR_FILE}"; then
        t_pass "XIANIX_RUNTIME_AUTODETECT=0 ignores the repo's version files"
    else
        t_fail "expected no declarations to be found with autodetect disabled and no plugins"
    fi
}

test_runtime_fallback_hook_hermetic() {
    banner "Test 6c: on-demand runtime fallback hook (hermetic)"

    # No plugins, no worktree: the "declares nothing" case the hook exists for.
    # Everything asserted here happens before any download, so it runs offline.
    local run_cmd='
        mkdir -p /tmp/bin
        printf "#!/usr/bin/env bash\nexit 0\n" > /tmp/bin/claude
        chmod +x /tmp/bin/claude
        export PATH="/tmp/bin:${PATH}"
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
        # A hook-installed runtime is never "declared", so the provisioner never
        # records its use; without the hook doing it, maintenance would adopt the
        # runtime once and then prune it at the retention window however heavily
        # it is used. Exercise that bookkeeping directly against a stand-in
        # install layout, so the assertion needs no multi-hundred-MB download.
        # shellcheck disable=SC1090
        . "${BASH_ENV}"
        mkdir -p "${MISE_DATA_DIR}/installs/faketool"
        ln -sfn ./1.2.3 "${MISE_DATA_DIR}/installs/faketool/latest"
        xianix_mark_runtime_used faketool
        ls /workspace/runtimes/.meta | sed "s/^/meta: /"
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

    # cargo is provided by the `rust` tool; without the alias table a Rust repo
    # would fall through even though the runtime is installable.
    if grep -q '^alias-cargo: resolving rust@latest' "${STDOUT_FILE}"; then
        t_pass "binary-to-tool alias maps 'cargo' onto the rust runtime"
    else
        t_fail "expected 'cargo' to resolve to rust@latest"
    fi

    # mvn needs the JDK *and* maven — mise's maven brings no Java of its own.
    if grep -q '^alias-mvn: resolving java@latest maven@latest' "${STDOUT_FILE}"; then
        t_pass "'mvn' co-installs the JDK alongside maven"
    else
        t_fail "expected 'mvn' to resolve to java@latest + maven@latest"
    fi

    # The marker must name the concrete version `mise uninstall` accepts, not the
    # `latest` alias — a marker per alias could uninstall a version still in use.
    if grep -qx 'meta: faketool@1.2.3.last-used' "${STDOUT_FILE}"; then
        t_pass "hook records runtime use so maintenance won't prune it while active"
    else
        t_fail "expected a faketool@1.2.3 last-used marker: $(grep '^meta:' "${STDOUT_FILE}" | tr '\n' ' ')"
    fi

    # And the whole thing must be switchable off.
    : > "${STDOUT_FILE}"
    set +e
    docker run --rm --network none \
        -e XIANIX_RUNTIME_FALLBACK=0 \
        -v "${VOLUME_RUNTIMES}:/workspace/runtimes" \
        --entrypoint bash "${IMAGE}" -c '
            mkdir -p /tmp/bin; printf "#!/usr/bin/env bash\nexit 0\n" > /tmp/bin/claude
            chmod +x /tmp/bin/claude; export PATH="/tmp/bin:${PATH}"
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
    # `dotnet --version` runs the *managed* CLI, unlike `dotnet --list-sdks`
    # which the native muxer answers on its own. Only the former catches a
    # missing runtime dependency in the image (libicu, openssl, …), which is
    # exactly what would break a plugin's `dotnet build` while every cheaper
    # smoke check still looked green — so assert on both.
    local run_cmd='
        set -e
        mkdir -p /tmp/bin /tmp/live-plugin
        printf "dotnet 9.0\n" > /tmp/live-plugin/.tool-versions
        cat > /tmp/bin/claude <<"SHIM"
#!/usr/bin/env bash
if [ "${1:-}" = "plugin" ] && [ "${2:-}" = "list" ]; then
    echo "[{\"id\":\"live-plugin@test\",\"installPath\":\"/tmp/live-plugin\"}]"
fi
exit 0
SHIM
        chmod +x /tmp/bin/claude
        export PATH="/tmp/bin:${PATH}"
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

    # Same SDK, declared the standard way by the repo instead of by a plugin:
    # must resolve out of the cache the plugin manifest populated.
    : > "${STDERR_FILE}"
    set +e
    docker run --rm \
        -v "${live_volume}:/workspace/runtimes" \
        -e WORK_DIR=/tmp/worktree \
        --entrypoint bash "${IMAGE}" -c '
            mkdir -p /tmp/bin /tmp/worktree
            printf "#!/usr/bin/env bash\nexit 0\n" > /tmp/bin/claude && chmod +x /tmp/bin/claude
            export PATH="/tmp/bin:${PATH}"
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

# ── Tier 2: real Claude Code run (needs ANTHROPIC_API_KEY) ────────────────────

test_claude_reads_planted_secret() {
    banner "Test 8: Claude Code reads the planted secret token from the repo"

    if [ -z "${ANTHROPIC_API_KEY:-}" ]; then
        t_skip "ANTHROPIC_API_KEY is not set — skipping the live Claude Code test."
        return
    fi

    local prompt="Read the file SECRET.md at the root of this repository. It contains a secret token. Reply with the exact token and nothing else."

    # Cost guards: cheapest model, few turns, hard budget cap. A normal pass
    # costs well under one cent and finishes in about a minute.
    run_executor "${VOLUME_MAIN}" \
        -e "TENANT-ID=integration-test" \
        -e "EXECUTION-ID=it-claude-1" \
        -e "XIANIX-INPUTS=${XIANIX_INPUTS_JSON}" \
        -e "CLAUDE-CODE-PLUGINS=[]" \
        -e "PROMPT=${prompt}" \
        -e "ANTHROPIC-API-KEY=${ANTHROPIC_API_KEY}" \
        -e "XIANIX-MODEL=claude-haiku-4-5" \
        -e "XIANIX-MAX-TURNS=10" \
        -e "XIANIX-MAX-BUDGET-USD=0.25"

    if [ "${LAST_EXIT}" -eq 0 ]; then
        t_pass "container exited 0"
    else
        t_fail "container exited ${LAST_EXIT}, expected 0"
        return
    fi

    # Stdout purity: the result must be exactly one line of valid JSON. A very
    # easy regression is someone adding an `echo` without `>&2` in a script.
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

    if jq -e '(.session_id // "") != "" and (.input_tokens // 0) > 0 and (.cost_usd // 0) > 0' \
            "${STDOUT_FILE}" >/dev/null; then
        t_pass "envelope metrics are populated (session_id, input_tokens, cost_usd)"
    else
        t_fail "envelope metrics missing: $(jq -c '{session_id, input_tokens, cost_usd}' "${STDOUT_FILE}")"
    fi

    echo
    echo "  Result envelope:"
    jq -r '
        "    agent reply : \(.result // "<none>" | gsub("\n"; " ") | .[0:120])",
        "    model(s)    : \(.models // [] | join(", "))",
        "    cost        : $\(.cost_usd // 0 | . * 10000 | round / 10000)",
        "    tokens      : in=\(.input_tokens // 0) out=\(.output_tokens // 0) cache_read=\(.cache_read_tokens // 0)",
        "    session     : \(.session_id // "<none>")"
    ' "${STDOUT_FILE}"
}

# ── Main ──────────────────────────────────────────────────────────────────────

for tool in docker git jq python3; do
    command -v "${tool}" >/dev/null || { echo "FATAL: '${tool}' is required." >&2; exit 1; }
done

banner "Unit: host_context preamble"
if python3 "${SCRIPT_DIR}/test_host_context.py"; then
    t_pass "host_context.py unit tests"
else
    t_fail "host_context.py unit tests"
fi

if [ -z "${SKIP_BUILD:-}" ]; then
    banner "Building executor image (${IMAGE})"
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
test_claude_reads_planted_secret

banner "Results"
if [ "${TESTS_FAILED}" -eq 0 ]; then
    echo "  ${C_GREEN}${C_BOLD}${TESTS_RUN} checks run, all passed.${C_RESET}"
else
    echo "  ${C_RED}${C_BOLD}${TESTS_RUN} checks run, ${TESTS_FAILED} failed.${C_RESET}"
fi
[ "${TESTS_FAILED}" -eq 0 ]

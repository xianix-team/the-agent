# Xianix Executor

The `xianix-executor` Docker image runs inside an isolated container per tenant event. It maintains a bare clone of the target Git repository on a persistent volume, creates an isolated git worktree per execution, installs Claude Code plugins, and runs a prompt against the codebase. Results are returned via **stdout** as structured JSON; progress logs go to **stderr**.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Image definition — Python 3.12, Node.js 20, git, gh CLI, Claude Code CLI + SDK |
| `entrypoint.sh` | Thin dispatcher — picks `prepare_repo.sh` and/or `run_prompt.sh` based on `XIANIX-MODE` |
| `prepare_repo.sh` | Configures git credentials, bare-clone-or-fetches the repo into `/workspace/repo`, and **always pulls the upstream default branch** (so `git diff origin/<default>` and the worktree from HEAD see the freshest tip on every run, for both webhook and chat-driven executions). In `prepare-and-execute` mode it also creates the per-execution worktree at `/workspace/exec-${EXECUTION-ID}`. |
| `run_prompt.sh` | Installs Claude Code plugins, provisions plugin-declared runtimes, prepares cached repo context, launches `execute_plugin.py`, then cleans up the worktree (from an `EXIT` trap, so cleanup runs even when the run exits non-zero) |
| `maintain_volume.sh` | Best-effort volume housekeeping run during prepare — `git gc --auto` on the bare clone, prunes superseded plugin-cache versions, expires old `xianix-sessions` pointers, reaps runtimes unused for `XIANIX-RUNTIME-MAX-AGE-DAYS` (default 30), and wipes the NuGet cache when it exceeds `XIANIX-NUGET-CACHE-MAX-MB` (default 4096). Never fails a run; retention windows are tunable via `XIANIX-SESSION-RETENTION-DAYS` / `XIANIX-PLUGIN-CACHE-MAX-AGE-DAYS`. |
| `provision_runtimes.sh` | Installs plugin-declared runtimes (see *Runtime harness* below) onto the shared runtime volume after plugin install, and emits the env exports (`PATH`, `DOTNET_ROOT`, `NUGET_PACKAGES`, …) that `run_prompt.sh` sources before launching Claude Code. User-space installs only; cached across runs; best-effort per runtime. |
| `_common.sh` | Shared helpers sourced by both phase scripts (env aliasing, `log`, input parsing, `configure_credentials`) |
| `generate_context.sh` | Builds a cached `CLAUDE.md` + `.xianix/repomap.txt` (symbol map) for the repo so the agent doesn't re-explore the codebase cold on every run. The facts (overview, stack, layout, symbols) are deterministic and token-free; optionally (`XIANIX-CONTEXT-LLM=1`) a budget-/turn-capped Haiku pass appends an "Architecture & conventions" narrative. Cached on the volume keyed by HEAD (so the LLM pass runs at most once per HEAD change); never overwrites a tenant-authored `CLAUDE.md`, and the LLM pass is skipped entirely when one exists. |
| `execute_plugin.py` | Invokes Claude Code SDK against the worktree; writes JSON result to stdout |
| `requirements.txt` | Python dependencies (pinned) |
| `.dockerignore` | Build context exclusions |
| `tests/integration_test.sh` | Black-box integration tests against the built image (see *Integration tests* below) |

### Execution modes (`XIANIX-MODE`)

| Mode | What runs | Use case |
|------|-----------|----------|
| `prepare-and-execute` *(default)* | `prepare_repo.sh` then `run_prompt.sh` | Webhook flows and chat-driven `RunClaudeCodeOnRepository`. Identical to the pre-split behaviour. |
| `prepare` | `prepare_repo.sh` only (bare clone, **no** worktree, no plugins, no prompt) | Chat-driven `OnboardRepository`: add a new repo to the tenant without running anything against it. |
| `execute` | `run_prompt.sh` only — assumes the workspace already exists | Reserved for future composite flows; not currently emitted by the control plane. |

## Building the image

```bash
cd Executor/
docker build -t xianix-executor:latest .
```

## Running locally for testing

The image expects all configuration via environment variables:

```bash
docker run --rm \
  -e TENANT-ID=local-test \
  -e EXECUTION-ID=test-001 \
  -e 'XIANIX-INPUTS={"repository-url":"https://github.com/your-org/your-repo","platform":"github"}' \
  -e CLAUDE-CODE-PLUGINS='[{"plugin-name":"github@claude-plugins-official","marketplace":"anthropics/claude-plugins-official"}]' \
  -e PROMPT="Review this repository and summarize the architecture." \
  -e ANTHROPIC-API-KEY=sk-ant-... \
  -e GITHUB-TOKEN=ghp_... \
  -v xianix-test-vol:/workspace/repo \
  xianix-executor:latest
```

### Persistent volume across runs

The `/workspace/repo` mount holds a **bare git clone**. On first run the repo is cloned; subsequent runs do a fast `git fetch`. Each execution creates an isolated git worktree from the bare repo — multiple concurrent executions against the same volume are safe.

Every run — webhook-triggered or chat-conversational — also re-pulls the upstream's **default branch** before any plugin or prompt action runs. This keeps `refs/heads/<default>` and the bare clone's `HEAD` in lock-step with the remote, so:

- A plugin doing `git diff origin/<default>` always sees the latest base.
- A worktree (`worktree add HEAD --detach`) always picks up the freshest default tip, even if a previous run left the bare clone on something else.
- An upstream default-branch rename (e.g. `master` → `main`) self-heals on the next execution rather than breaking subsequent worktree creations.

```bash
docker volume create xianix-test-vol

# First run — bare clone + worktree
docker run --rm -e ... -v xianix-test-vol:/workspace/repo xianix-executor:latest

# Second run — fetch + new worktree (previous clone reused)
docker run --rm -e ... -v xianix-test-vol:/workspace/repo xianix-executor:latest
```

## Runtime harness (plugin-declared runtimes)

The base image ships Python 3.12 and Node 20, but no .NET/Java/etc. Plugins that must
**build and run code** (e.g. a unit-test writer verifying its work with `dotnet test`)
declare the runtimes they need in a manifest at their plugin root:

```json
// xianix-runtimes.json
{
  "runtimes": [
    { "name": "dotnet", "version": "9.0" },
    { "name": "node",   "version": "22.11.0" }
  ]
}
```

After plugin install, `provision_runtimes.sh` collects the manifests of all installed
plugins and installs anything missing — **user-space only** (the container is non-root
with `no-new-privileges`, so nothing here needs or uses apt). The environment exports it
emits are sourced before Claude Code starts, so the tools are on `PATH` for the agent's
Bash tool, and the provisioned list is surfaced in the prompt's host-context block.

### Supported runtimes

| Name | Version forms | Install mechanism |
|------|---------------|-------------------|
| `dotnet` | channel (`9.0`, `LTS`, `STS`) or pinned (`9.0.203`) | official `dotnet-install.sh` (baked into the image at build time) into a shared root — multiple SDK versions coexist; the muxer resolves per `global.json`. Also exports a persistent `NUGET_PACKAGES` cache. |
| `node` | pinned (`22.11.0`) or prefix (`22`, `22.11` → newest match) | official tarball from nodejs.org into an isolated per-version dir. Only needed for versions other than the baked-in Node 20. |

Anything else in a manifest is rejected with a warning (allow-list — a manifest can never
make the executor fetch arbitrary URLs or run arbitrary commands). Failed installs are
non-fatal, mirroring plugin-install behaviour.

### Where runtimes live

The control plane mounts a **shared per-tenant runtime volume** (`xianix-{tenant}-runtimes`)
at `/workspace/runtimes` on every executor container. The cache is keyed by runtime
name+version, so one SDK download serves **every repo, every plugin, and every run** of the
tenant. When no runtime volume is mounted (plain local `docker run`, older control planes)
the provisioner falls back to `${REPO_DIR}/xianix-runtimes` on the repo volume.

```
/workspace/runtimes/
├── dotnet/                    ← shared SDK root (.xianix-ok-<version> markers)
├── node-22.11.0/              ← isolated per-version install
├── cache/nuget/               ← persistent NuGet global-packages cache
└── .meta/<name>-<ver>.last-used  ← touched every run; drives pruning
```

Concurrency: installs run under a `flock` on the volume (`.provision.lock`) with atomic
move-into-place, so concurrent containers — including ones serving *different repos* of the
same tenant — never clobber each other. Cache hits don't take the lock.

Housekeeping: `maintain_volume.sh` reaps runtimes whose `.last-used` marker is older than
`XIANIX-RUNTIME-MAX-AGE-DAYS` (default 30) and wipes the NuGet cache past
`XIANIX-NUGET-CACHE-MAX-MB` (default 4096).

### Sizing notes

- A .NET SDK is ~200MB to download and ~1.2GB on disk **per version per tenant**; the NuGet
  cache adds 0.5–3GB+ for real solutions (bounded by the cap above).
- The first run that requests a runtime pays the download inside the container's wall-clock
  budget — raise `CONTAINER-EXECUTION-TIMEOUT-SECONDS` (default 900) for build/test plugins.
- `CONTAINER-MEMORY-MB=1024` / 1 CPU is tight for `dotnet build`; 2048+ is recommended for
  tenants using build/test plugins.

### Trying it locally

```bash
docker volume create xianix-test-runtimes
docker run --rm \
  -e ... \
  -v xianix-test-vol:/workspace/repo \
  -v xianix-test-runtimes:/workspace/runtimes \
  xianix-executor:latest
```

## Integration tests

`tests/integration_test.sh` verifies the executor end to end, treating the image exactly the way the control plane does: environment variables in, one JSON envelope on stdout, logs on stderr, exit code out.

Instead of a real GitHub repository, the harness generates a small **local fixture repository** on the fly and mounts it into the container. The executor clones from that local path, so the tests need no network, no GitHub token, and no external test repo.

The tests run in two tiers:

| Tier | Needs | What it verifies |
|------|-------|------------------|
| 0 — unit (always runs, free) | Python 3 only | `host_context.py` prepends `[Xianix host context]` when `platform` is set; skips when absent; idempotent; renders the provisioned-runtimes line |
| 1 — hermetic (always runs, free) | Docker only | `prepare` mode clones onto the volume; a second run fetches instead of re-cloning; a new upstream commit is picked up (default-branch refresh contract); a bad repo URL emits the structured prepare error envelope with a non-zero exit; the runtime provisioner honours manifests, reuses a pre-seeded cache without downloading, emits the env exports, and rejects unsupported/invalid entries |
| R — live runtimes (opt-in via `XIANIX_IT_RUNTIMES=1`, ~200MB download) | Docker + network | A real .NET SDK install onto a runtime volume; a second run on the same volume must hit the cache instead of re-downloading |
| 2 — live Claude Code (skipped without a key, costs ~$0.03) | `ANTHROPIC_API_KEY` | A full `prepare-and-execute` run. The fixture contains a `SECRET.md` with a **random token generated fresh per test run**; the prompt asks the agent to read the file and reply with the token. The test asserts the token appears in `.result` — which is only possible if the agent genuinely cloned the repo, created the worktree, and read the file. Also asserts stdout purity (exactly one line of valid JSON) and that `session_id` / `input_tokens` / `cost_usd` are populated. The run is cost-capped via `XIANIX-MODEL=claude-haiku-4-5`, `XIANIX-MAX-TURNS=10`, and `XIANIX-MAX-BUDGET-USD=0.25`. |

### Running locally

```bash
# Tier 1 only (builds the image first):
./Executor/tests/integration_test.sh

# Both tiers:
ANTHROPIC_API_KEY=sk-ant-... ./Executor/tests/integration_test.sh
# (or put ANTHROPIC_API_KEY=... in the gitignored Executor/tests/.env —
#  the harness loads it automatically when the env var isn't already set)

# Reuse an image you already built:
SKIP_BUILD=1 IMAGE=xianix-executor:latest ./Executor/tests/integration_test.sh

# Quiet mode — container logs are hidden unless a check fails:
SHOW_LOGS=0 ./Executor/tests/integration_test.sh
```

Each container's log stream (clone progress, plugin install, the executor's turn-by-turn Claude activity) is echoed live under the test it belongs to, dimmed and gutter-prefixed so the `PASS`/`FAIL` lines stay easy to scan. The Claude test also prints a short summary of the returned envelope (agent reply, model, cost, tokens, session id).

### In CI

The `integration-test` job in `.github/workflows/publish-executor.yml` runs on every PR that touches `Executor/` and gates every tag-triggered publish (`build-and-push` depends on it). It builds a single-arch test image (sharing the GHA layer cache with the publish build) and runs the harness; the live Claude test runs only when the `ANTHROPIC_API_KEY` repo secret is configured, and is skipped gracefully otherwise (e.g. on fork PRs).

### Capturing stdout vs stderr

```bash
docker run ... xianix-executor:latest \
  1>result.json \
  2>progress.log

cat result.json   # structured JSON from the executor
cat progress.log  # git + plugin + executor progress messages
```

## Environment variables reference

| Variable | Required | Description |
|----------|----------|-------------|
| `TENANT-ID` | Yes | Identifies the tenant for logging and isolation |
| `EXECUTION-ID` | Yes | Unique per-execution ID, used as the git worktree name |
| `XIANIX-MODE` | No | Phase selector — `prepare-and-execute` (default), `prepare` (bare clone only), or `execute` (run an already-prepared workspace). See *Execution modes* above. |
| `XIANIX-INPUTS` | Yes | JSON object with dynamic inputs. For repo-bound runs the agent auto-injects the structural keys `repository-url` and `platform` from the execution-level `repository` / `platform` fields in `rules.json`. The short `repository-name` (e.g. `owner/repo`) is **derived** from `repository-url` (platform-aware: handles GitHub, Azure DevOps `_git` URLs, etc.) and injected alongside them. None of these keys are authored under `use-inputs`. The worktree always starts on the default-branch HEAD; task-specific checkouts are performed by plugins. |
| `CLAUDE-CODE-PLUGINS` | Yes | JSON array of `{ "plugin-name", "marketplace"? }` plugin descriptors. Env vars used by the plugins are injected separately by the agent via the execution-level `with-envs` in `rules.json` and never appear in this payload. |
| `PROMPT` | Yes | Fully interpolated Claude Code prompt to execute. When `XIANIX_INPUTS` includes a non-empty `platform`, the executor prepends a short `[Xianix host context]` block (platform + optional `repository-name`) so Claude Code / plugins can pick the right host APIs without relying on env alone. |
| `ANTHROPIC-API-KEY` | Yes | Anthropic API key (read by the Claude Code SDK) |
| `GITHUB-TOKEN` | Conditional | GitHub PAT — required for GitHub workflows (clones, marketplace repos, `gh` CLI). Injected from the **tenant Secret Vault** via `"value": "secrets.GITHUB-TOKEN"` in `rules.json`; never read from the agent host. |
| `AZURE-DEVOPS-TOKEN` | Conditional | Azure DevOps PAT — required when `platform=azuredevops`. Injected from the **tenant Secret Vault** via `"value": "secrets.AZURE-DEVOPS-TOKEN"` in `rules.json`; never read from the agent host. |

> **Note:** The entrypoint automatically re-exports dashed env vars as underscored aliases (e.g. `GITHUB-TOKEN` → `GITHUB_TOKEN`) for bash compatibility.

> **Multi-tenant:** Platform tokens are scoped per tenant — there is no host-level fallback. A tenant whose `secrets.GITHUB-TOKEN` is missing will fail-fast with a non-retryable error (when the rule marks it `mandatory: true`) rather than silently borrow another tenant's credential.

### Inputs extracted from `XIANIX_INPUTS`

| Key | Used for |
|-----|----------|
| `repository-url` | Git clone/fetch target. Required for repo-bound runs; framework-managed (injected from the execution-level `repository.url` in `rules.json`). |
| `platform` | Credential selection: `github` (default), `azuredevops`. Framework-managed (injected from the execution-level `platform`). Also used to prepend host context onto `PROMPT` when non-empty. |
| `conversation-id` | Optional opaque id used (filename-sanitised) as the session-resume key when `XIANIX-RESUME-SESSIONS` is enabled. Framework-managed — injected from the execution-level `conversation-key` binding in `rules.json` (e.g. mapped from the payload's PR id); the executor attaches no meaning to its contents. |

The executor shell scripts read **only** these structural keys from `XIANIX_INPUTS`. All other inputs (`pr-number`, `issue-number`, …) are task-specific and opaque to the executor — they reach the plugin through the interpolated `PROMPT`, keeping the executor independent of any particular action.

## Concurrency model

The executor uses **git worktrees** to support concurrent execution against the same tenant+repo volume:

```
/workspace/repo/              ← bare clone (shared object store, on volume)
/workspace/exec-<exec-id>/    ← isolated worktree per execution (ephemeral)
```

Multiple containers can mount the same volume simultaneously. Each creates its own worktree from the shared bare repo, runs independently, and cleans up its worktree on exit. Orphaned worktrees from crashed containers are pruned on the next run.

## Publishing to Docker Hub

The image is published to **`99xio/xianix-executor`** on Docker Hub via a GitHub Actions workflow.

### Automatic publishing (CI)

The workflow at `.github/workflows/executor-dockerhub-deploy.yml` triggers on version tags:

```bash
# Tag a release — triggers the build automatically (bash / zsh)
VERSION=v1.0.0
git tag $VERSION
git push origin $VERSION
```

On Windows PowerShell:

```powershell
$VERSION = "v1.0.0"
git tag $VERSION
git push origin $VERSION
```

This produces multi-arch images (`linux/amd64` + `linux/arm64`) with semver tags:

| Git tag | Docker Hub tags |
|---------|-----------------|
| `v1.2.3` | `1.2.3`, `1.2`, `1`, `latest` |
| `v2.0.0-beta.1` | `2.0.0-beta.1` (no `latest`) |

The workflow can also be triggered manually from the Actions tab via `workflow_dispatch`.

### Required secrets

The workflow uses the following GitHub Actions secret (configured in repo settings):

| Secret | Description |
|--------|-------------|
| `DOCKERHUB_TOKEN` | Docker Hub access token for the `hasithy99x` account |

### Manual publishing

To build and push locally without CI:

```bash
cd Executor/

# Build for the current platform
docker build -t 99xio/xianix-executor:latest .
docker push 99xio/xianix-executor:latest

# Build and push a specific version
docker build -t 99xio/xianix-executor:1.0.0 .
docker push 99xio/xianix-executor:1.0.0

# Multi-arch build (requires buildx)
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t 99xio/xianix-executor:1.0.0 \
  -t 99xio/xianix-executor:latest \
  --push .
```

### Pulling the image

```bash
docker pull 99xio/xianix-executor:latest
```

The control plane defaults to `99xio/xianix-executor:latest` (configurable via the `EXECUTOR-IMAGE` environment variable).

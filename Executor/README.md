# Xianix Executor

The `xianix-executor` Docker image runs inside an isolated container per tenant event. It maintains a bare clone of the target Git repository on a persistent volume, creates an isolated git worktree per execution, installs Claude Code plugins, and runs a prompt against the codebase. Results are returned via **stdout** as structured JSON; progress logs go to **stderr**.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Image definition — Python 3.12, Node.js 20, git, gh CLI, Claude Code CLI + SDK, `mise` (on-demand runtime installs) |
| `entrypoint.sh` | Thin dispatcher — picks `prepare_repo.sh` and/or `run_prompt.sh` based on `XIANIX-MODE` |
| `prepare_repo.sh` | Configures git credentials, bare-clone-or-fetches the repo into `/workspace/repo`, and **always pulls the upstream default branch** (so `git diff origin/<default>` and the worktree from HEAD see the freshest tip on every run, for both webhook and chat-driven executions). In `prepare-and-execute` mode it also creates the per-execution worktree at `/workspace/exec-${EXECUTION-ID}`. |
| `run_prompt.sh` | Installs Claude Code plugins, provisions runtimes, prepares cached repo context, launches `execute_plugin.py`, then cleans up the worktree (from an `EXIT` trap, so cleanup runs even when the run exits non-zero) |
| `maintain_volume.sh` | Best-effort volume housekeeping run during prepare — `git gc --auto` on the bare clone, prunes superseded plugin-cache versions, expires old `xianix-sessions` pointers, uninstalls runtimes unused for `XIANIX-RUNTIME-MAX-AGE-DAYS` (default 30), and wipes the NuGet cache when it exceeds `XIANIX-NUGET-CACHE-MAX-MB` (default 4096). Never fails a run; retention windows are tunable via `XIANIX-SESSION-RETENTION-DAYS` / `XIANIX-PLUGIN-CACHE-MAX-AGE-DAYS`. |
| `provision_runtimes.sh` | Drives `mise` to install the runtimes the repo and the installed plugins declare (see *Runtime harness* below) onto the shared runtime volume, and emits the env exports (`PATH`, `DOTNET_ROOT`, `NUGET_PACKAGES`, …) that `run_prompt.sh` sources before launching Claude Code. User-space installs only; cached across runs; best-effort per runtime. |
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

## Runtime harness

The base image ships Python 3.12 and Node 20, but no .NET/Java/etc. Anything else is
installed on demand by [mise](https://mise.jdx.dev), a user-space runtime manager baked
into the image as a single pinned, checksum-verified binary. `provision_runtimes.sh`
drives it after plugin install; the exports it emits are sourced before Claude Code starts,
so the tools are on `PATH` for the agent's Bash tool, and the provisioned list is surfaced
in the prompt's host-context block.

### Declaring runtimes

There is **no Xianix-specific manifest format**. Runtimes are declared with the standard
files the ecosystem already uses, from two sources that are merged:

**1. The repository being worked on** — authoritative, because the repo knows which SDK it
builds with. mise reads whatever version file the repo already has, so most repos need no
new file at all:

| Runtime | Files read from the worktree |
|---------|------------------------------|
| `dotnet` | `global.json` |
| `node` | `.nvmrc`, `.node-version` |
| `python` | `.python-version`, `.python-versions` |
| `go` | `go.mod`, `.go-version` |
| `java` | `.java-version`, `.sdkmanrc` |
| `ruby` | `.ruby-version`, `Gemfile` |
| `rust` | `rust-toolchain.toml` |
| `bun` / `deno` / `zig` / `elixir` | `.bun-version`, `.deno-version`, `.zig-version`, `.exenv-version` |
| *(any)* | `mise.toml`, `.tool-versions` |

Set `XIANIX-RUNTIME-AUTODETECT=0` to switch this off and honour plugin manifests only.

> **`package.json` is not a node version source.** mise's node plugin reads `.nvmrc` and
> `.node-version` only — `engines.node` and `volta` are ignored. A repo that wants a
> specific Node version from the executor needs one of those two files; otherwise it gets
> the image's Node 20.

**2. Plugin manifests** — a `.tool-versions` file at the plugin root, for plugins that must
build and run code (e.g. a unit-test writer verifying with `dotnet test`) against repos that
declare nothing:

```
dotnet 9.0
node   22.11.0
```

Plugin entries are written into a generated mise *global* config, which is the
lowest-precedence config file — so a repository declaration always overrides the plugin's,
and the plugin entry acts as the fallback it should be.

**3. On first use, if nothing declared it** — the safety net beneath the other two. The
image ships only Python and Node, so a repo that needs any other runtime and declares no
version gets a hard `command not found` (`global.json` is optional in .NET, and most repos
omit it). The provisioner therefore installs a `command_not_found_handle` into the agent's
shells via `BASH_ENV`, which defers to mise's own auto-install:

```
$ dotnet build          # .csproj present, no global.json
[runtimes] 'dotnet' is not installed and no version file declares it — installing
           dotnet@latest into the tenant cache. Pin a version to install it up front instead.
```

It is gated on the same allow-list as the eager path, so a typo or a non-runtime binary is
refused immediately with the usual 127 and no registry lookup. A binary→tools table covers
commands whose name differs from the tool providing them, and maps to a *set* where one
tool isn't enough: `cargo`/`rustc`→`rust`, `javac`→`java`, and `mvn`→`java`+`maven` (mise's
`maven` ships no JDK, so on its own `mvn` dies with "JAVA_HOME is not defined correctly").
Set `XIANIX-RUNTIME-FALLBACK=0` to disable.

`maven` and `gradle` are the only non-runtimes on the allow-list, for a structural reason:
every other ecosystem ships its build tool inside the runtime — the .NET SDK carries
MSBuild, node carries npm, go carries the go tool, rust carries cargo, python carries pip —
whereas the JDK ships none, which would otherwise leave every Maven/Gradle repo unbuildable.

Note the hook only fires for a command that is *absent*. A repo driven by the `./mvnw` or
`./gradlew` wrapper scripts runs a file that does exist, so it fails inside the wrapper with
a Java error instead. Those repos need a `.java-version` or `.sdkmanrc`.

This resolves `latest` rather than a pinned version and pays the download mid-run, so it is
strictly a fallback: declaring a version file is still better on both counts, and when one
exists the tool is already on `PATH` and the hook never fires.

#### What the container log shows

Everything the hook prints — its own message and mise's download progress — goes to the
stderr of the *agent's* Bash call, which Claude Code captures as tool output. So an on-demand
install leaves no trace in the executor log, and a failed one leaves none either. The hook
therefore appends each resolve to a per-execution ledger which `run_prompt.sh` replays once
the agent exits, giving an operator something to attribute a slow run or a grown tenant volume
to:

```
[runtimes] No plugin manifest and no repository version file — nothing to install up front.
[runtimes] Runtime cache root: /workspace/runtimes — on-demand fallback armed for: bun deno …
  … agent runs …
[runtimes] Fetched on demand: java 26.0.2 (389M), maven 3.9.16 (11M)
```

A cache hit reports as `Reused from cache: java 26.0.2` with no size, and an install that
never landed as `WARNING: on-demand install FAILED: java@latest`. A tool downloaded on its
first call is reported only as fetched, not also as cached on subsequent ones. The ledger
lives in `/tmp` rather than on the volume on purpose: tenants share the volume, so a ledger
kept there would attribute a concurrent container's download to this execution.

### Version forms and the allow-list

Versions may be fuzzy (`9.0`, `22`), pinned (`9.0.203`, `22.11.0`), or an alias
(`lts`, `latest`); mise resolves them to a concrete release. Only language runtimes are
installable — the default allow-list is `bun deno dotnet elixir erlang go java kotlin node
python ruby rust scala swift zig`, overridable via `XIANIX-RUNTIME-ALLOWED-TOOLS`. Anything
else in a plugin manifest is rejected with a warning, as are `ref:`/`path:` version scopes
and mise's explicit backend syntax (`cargo:foo`, `asdf:<git-url>`). Failed installs are
non-fatal, mirroring plugin-install behaviour.

Security posture: mise runs with `MISE_SAFE=1`, a hard code-execution boundary — a config
in a plugin or repository can declare tool versions and nothing else (no tasks, no
postinstall hooks, no `[env]` injection into the Claude Code process, and no overriding
mise's own settings). The code-executing backends (`asdf`, `vfox`) and the
compile-from-source ones (`cargo`, `npm`, `pipx`, `gem`, `spm`) are disabled outright.
Downloads are checksum-verified by mise, and Node tarballs additionally have their OpenPGP
signature checked.

### Where runtimes live

The control plane mounts a **shared per-tenant runtime volume** (`xianix-{tenant}-runtimes`)
at `/workspace/runtimes` on every executor container. The cache is keyed by tool+version, so
one SDK download serves **every repo, every plugin, and every run** of the tenant. When no
runtime volume is mounted (plain local `docker run`, older control planes) the provisioner
falls back to `${REPO_DIR}/xianix-runtimes` on the repo volume.

```
/workspace/runtimes/
├── mise/
│   ├── installs/<tool>/<version>/   ← one dir per tool+version
│   └── dotnet-root/                 ← shared DOTNET_ROOT; SDKs side by side,
│                                      the muxer resolves per global.json
├── mise-cache/                      ← download + registry metadata cache
├── mise-state/
├── cache/nuget/                     ← persistent NuGet global-packages cache
└── .meta/<tool>@<ver>.last-used     ← touched every run; drives pruning
```

Concurrency: installs run under a `flock` on the volume (`.provision.lock`), so concurrent
containers — including ones serving *different repos* of the same tenant — never clobber
each other. A fully warm cache skips the lock entirely.

Housekeeping: `maintain_volume.sh` `mise uninstall`s runtimes whose `.last-used` marker is
older than `XIANIX-RUNTIME-MAX-AGE-DAYS` (default 30), prunes mise's download cache, and
wipes the NuGet cache past `XIANIX-NUGET-CACHE-MAX-MB` (default 4096).

### Sizing notes

- A .NET SDK is ~210MB to download and ~600MB on disk **per version per tenant** (measured:
  9.0.316 unpacks to 604MB); the NuGet cache adds 0.5–3GB+ for real solutions (bounded by
  the cap above).
- The first run that requests a runtime pays the download inside the container's wall-clock
  budget — raise `CONTAINER-EXECUTION-TIMEOUT-SECONDS` (default 900) for build/test plugins.
- `CONTAINER-MEMORY-MB=1024` / 1 CPU is tight for `dotnet build`; 2048+ is recommended for
  tenants using build/test plugins.
- Parallel builds need process headroom, not just CPU. `nproc` inside the container reports
  the *host's* core count — `--cpus` throttles CPU time but doesn't change CPU affinity, and
  the only knob that would (`--cpuset-cpus`) pins tenants to fixed cores — so a 1-CPU
  container still fans out one worker per host core. `CONTAINER-PIDS-LIMIT` (default 2048)
  has to clear that watermark; too low and builds die mid-flight with `pthread_create:
  Resource temporarily unavailable`. The host also seeds `DOTNET_PROCESSOR_COUNT`,
  `GOMAXPROCS`, `CARGO_BUILD_JOBS`, `RAYON_NUM_THREADS` and `MAKEFLAGS` from
  `CONTAINER-CPU-COUNT` so those toolchains right-size themselves; Node has no equivalent
  knob and relies on the pid ceiling. Any of them can be overridden per rule via `with-envs`.

### Trying it locally

```bash
docker volume create xianix-test-runtimes
docker run --rm \
  -e ... \
  -v xianix-test-vol:/workspace/repo \
  -v xianix-test-runtimes:/workspace/runtimes \
  xianix-executor:latest
```

### Bumping mise

```bash
docker build --build-arg MISE_VERSION=v2026.9.0 -t xianix-executor:latest Executor/
```

## Integration tests

`tests/integration_test.sh` verifies the executor end to end, treating the image exactly the way the control plane does: environment variables in, one JSON envelope on stdout, logs on stderr, exit code out.

Instead of a real GitHub repository, the harness generates a small **local fixture repository** on the fly and mounts it into the container. The executor clones from that local path, so the tests need no network, no GitHub token, and no external test repo.

The tests run in two tiers:

| Tier | Needs | What it verifies |
|------|-------|------------------|
| 0 — unit (always runs, free) | Python 3 only | `host_context.py` prepends `[Xianix host context]` when `platform` is set; skips when absent; idempotent; renders the provisioned-runtimes line |
| 1 — hermetic (always runs, free) | Docker only | `prepare` mode clones onto the volume; a second run fetches instead of re-cloning; a new upstream commit is picked up (default-branch refresh contract); a bad repo URL emits the structured prepare error envelope with a non-zero exit; the runtime provisioner (run with `--network none`) collects plugin `.tool-versions`, detects the repo's own version files, rejects non-allow-listed tools / backend-prefixed names / traversal versions, and stays non-fatal when the install can't reach the network |
| R — live runtimes (opt-in via `XIANIX_IT_RUNTIMES=1`, ~200MB download) | Docker + network | A real .NET SDK install onto a runtime volume driven by a plugin manifest, then by a repo `global.json`; a second run on the same volume must hit the cache instead of re-downloading |
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

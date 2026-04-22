# Xianix Executor

The `xianix-executor` Docker image runs inside an isolated container per tenant event. It maintains a bare clone of the target Git repository on a persistent volume, creates an isolated git worktree per execution, installs Claude Code plugins, and runs a prompt against the codebase. Results are returned via **stdout** as structured JSON; progress logs go to **stderr**.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Image definition — Python 3.12, Node.js 20, git, gh CLI, Claude Code CLI + SDK |
| `entrypoint.sh` | Thin dispatcher — picks `prepare_repo.sh` and/or `run_prompt.sh` based on `XIANIX-MODE` |
| `prepare_repo.sh` | Configures git credentials and bare-clone-or-fetches the repo into `/workspace/repo`. In `prepare-and-execute` mode it also creates the per-execution worktree at `/workspace/exec-${EXECUTION-ID}`. |
| `run_prompt.sh` | Installs Claude Code plugins, launches `execute_plugin.py`, then cleans up the worktree |
| `_common.sh` | Shared helpers sourced by both phase scripts (env aliasing, `log`, input parsing, `configure_credentials`) |
| `execute_plugin.py` | Invokes Claude Code SDK against the worktree; writes JSON result to stdout |
| `requirements.txt` | Python dependencies (pinned) |
| `.dockerignore` | Build context exclusions |

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
  -e 'XIANIX-INPUTS={"repository-url":"https://github.com/your-org/your-repo","platform":"github","git-ref":"feature/foo"}' \
  -e CLAUDE-CODE-PLUGINS='[{"plugin-name":"github@claude-plugins-official","marketplace":"anthropics/claude-plugins-official"}]' \
  -e PROMPT="Review this repository and summarize the architecture." \
  -e ANTHROPIC-API-KEY=sk-ant-... \
  -e GITHUB-TOKEN=ghp_... \
  -v xianix-test-vol:/workspace/repo \
  xianix-executor:latest
```

### Persistent volume across runs

The `/workspace/repo` mount holds a **bare git clone**. On first run the repo is cloned; subsequent runs do a fast `git fetch`. Each execution creates an isolated git worktree from the bare repo — multiple concurrent executions against the same volume are safe.

```bash
docker volume create xianix-test-vol

# First run — bare clone + worktree
docker run --rm -e ... -v xianix-test-vol:/workspace/repo xianix-executor:latest

# Second run — fetch + new worktree (previous clone reused)
docker run --rm -e ... -v xianix-test-vol:/workspace/repo xianix-executor:latest
```

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
| `XIANIX-INPUTS` | Yes | JSON object with dynamic inputs. For repo-bound runs the agent auto-injects the structural keys `repository-url`, `platform`, and (when declared) `git-ref` from the execution-level `repository` / `platform` fields in `rules.json`. The short `repository-name` (e.g. `owner/repo`) is **derived** from `repository-url` (platform-aware: handles GitHub, Azure DevOps `_git` URLs, etc.) and injected alongside them. None of these keys are authored under `use-inputs`. |
| `CLAUDE-CODE-PLUGINS` | Yes | JSON array of `{ "plugin-name", "marketplace"? }` plugin descriptors. Env vars used by the plugins are injected separately by the agent via the execution-level `with-envs` in `rules.json` and never appear in this payload. |
| `PROMPT` | Yes | Fully interpolated Claude Code prompt to execute |
| `ANTHROPIC-API-KEY` | Yes | Anthropic API key (read by the Claude Code SDK) |
| `GITHUB-TOKEN` | Conditional | GitHub PAT — required for GitHub workflows (clones, marketplace repos, `gh` CLI). Injected from the **tenant Secret Vault** via `"value": "secrets.GITHUB-TOKEN"` in `rules.json`; never read from the agent host. |
| `AZURE-DEVOPS-TOKEN` | Conditional | Azure DevOps PAT — required when `platform=azuredevops`. Injected from the **tenant Secret Vault** via `"value": "secrets.AZURE-DEVOPS-TOKEN"` in `rules.json`; never read from the agent host. |

> **Note:** The entrypoint automatically re-exports dashed env vars as underscored aliases (e.g. `GITHUB-TOKEN` → `GITHUB_TOKEN`) for bash compatibility.

> **Multi-tenant:** Platform tokens are scoped per tenant — there is no host-level fallback. A tenant whose `secrets.GITHUB-TOKEN` is missing will fail-fast with a non-retryable error (when the rule marks it `mandatory: true`) rather than silently borrow another tenant's credential.

### Inputs extracted from `XIANIX_INPUTS`

| Key | Used for |
|-----|----------|
| `repository-url` | Git clone/fetch target. Required for repo-bound runs; framework-managed (injected from the execution-level `repository.url` in `rules.json`). |
| `platform` | Credential selection: `github` (default), `azuredevops`. Framework-managed (injected from the execution-level `platform`). |
| `git-ref` | Ref (branch / commit / tag) to check out into the worktree. Framework-managed (injected from the execution-level `repository.ref` in `rules.json`). When the rule omits `repository.ref` the executor runs against the bare-clone HEAD. |

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

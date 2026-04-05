# Xianix Executor

The `xianix-executor` Docker image runs inside an isolated container per tenant event. It maintains a bare clone of the target Git repository on a persistent volume, creates an isolated git worktree per execution, installs Claude Code plugins, and runs a prompt against the codebase. Results are returned via **stdout** as structured JSON; progress logs go to **stderr**.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Image definition — Python 3.12, Node.js 20, git, gh CLI, Claude Code CLI + SDK |
| `entrypoint.sh` | Bare-clone-or-fetch + worktree creation + plugin install + launches `execute_plugin.py` |
| `execute_plugin.py` | Invokes Claude Code SDK against the worktree; writes JSON result to stdout |
| `requirements.txt` | Python dependencies (pinned) |
| `.dockerignore` | Build context exclusions |

## Building the image

```bash
cd Executor/
docker build -t xianix-executor:latest .
```

## Running locally for testing

The image expects all configuration via environment variables:

```bash
docker run --rm \
  -e TENANT_ID=local-test \
  -e EXECUTION_ID=test-001 \
  -e 'XIANIX_INPUTS={"repository-url":"https://github.com/your-org/your-repo","platform":"github","pr-head-branch":"feature/foo"}' \
  -e CLAUDE_CODE_PLUGINS='[{"name":"github","url":"github@claude-plugins-official","marketplace":"anthropics/claude-plugins-official"}]' \
  -e PROMPT="Review this repository and summarize the architecture." \
  -e ANTHROPIC_API_KEY=sk-ant-... \
  -e GITHUB_TOKEN=ghp_... \
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
| `TENANT_ID` | Yes | Identifies the tenant for logging and isolation |
| `EXECUTION_ID` | Yes | Unique per-execution ID, used as the git worktree name |
| `XIANIX_INPUTS` | Yes | JSON object with dynamic inputs (must include `repository-url`) |
| `CLAUDE_CODE_PLUGINS` | Yes | JSON array of `{ name, url, marketplace?, envs? }` plugin descriptors |
| `PROMPT` | Yes | Fully interpolated Claude Code prompt to execute |
| `ANTHROPIC_API_KEY` | Yes | Anthropic API key (read by the Claude Code SDK) |
| `GITHUB_TOKEN` | Conditional | GitHub PAT — always injected when available (clones, marketplace repos, `gh` CLI) |
| `AZURE_DEVOPS_TOKEN` | Conditional | Azure DevOps PAT — injected when `platform=azuredevops` |

### Inputs extracted from `XIANIX_INPUTS`

| Key | Used for |
|-----|----------|
| `repository-url` | Git clone/fetch target (required) |
| `platform` | Credential selection: `github` (default) or `azuredevops` |
| `pr-head-branch` | Optional ref to check out via worktree |

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

The control plane defaults to `99xio/xianix-executor:latest` (configurable via the `EXECUTOR_IMAGE` environment variable).

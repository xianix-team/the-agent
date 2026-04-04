# Xianix Executor

The `xianix-executor` Docker image runs inside an isolated container per tenant event. It clones (or refreshes) the target Git repository and invokes a Claude Code plugin against the codebase. Results are returned via **stdout** as structured JSON; progress logs go to **stderr**.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Image definition — Python 3.12, git, claude-code-sdk |
| `entrypoint.sh` | Clone-or-fetch logic + launches `execute_plugin.py` |
| `execute_plugin.py` | Invokes Claude Code SDK; writes JSON result to stdout |
| `requirements.txt` | Python dependencies |

## Building the image

```bash
cd Executor/
docker build -t xianix-executor:latest .
```

For a specific tag (used in CI):
```bash
docker build -t xianix-executor:1.0.0 .
docker tag xianix-executor:1.0.0 xianix-executor:latest
```

## Running locally for testing

The image expects all configuration via environment variables:

```bash
docker run --rm \
  -e TENANT_ID=local-test \
  -e REPOSITORY_URL=https://github.com/your-org/your-repo \
  -e PLATFORM=github \
  -e GITHUB_TOKEN=ghp_... \
  -e PLUGIN_NAME=pr-review \
  -e PLUGIN_COMMAND="/pr-review 42 https://github.com/your-org/your-repo" \
  -e LLM_API_KEY=sk-ant-... \
  -v xianix-test-vol:/workspace \
  xianix-executor:latest
```

### Persistent volume across runs

The `/workspace` mount is a named Docker volume. On first run the repo is cloned; subsequent runs do a fast `git fetch + reset`:

```bash
# Create the volume explicitly (or let Docker create it on first run)
docker volume create xianix-test-vol

# First run — clones repo
docker run --rm -e ... -v xianix-test-vol:/workspace xianix-executor:latest

# Second run — fetches and reuses
docker run --rm -e ... -v xianix-test-vol:/workspace xianix-executor:latest
```

### Capturing stdout vs stderr

```bash
# Separate stdout (JSON result) from stderr (progress logs)
docker run ... xianix-executor:latest \
  1>result.json \
  2>progress.log

cat result.json   # structured JSON from the plugin
cat progress.log  # git + executor progress messages
```

## Environment variables reference

| Variable | Required | Description |
|----------|----------|-------------|
| `TENANT_ID` | Yes | Identifies the tenant for logging |
| `REPOSITORY_URL` | Yes | Git repo to clone/fetch |
| `PLATFORM` | Yes | `github` or `azuredevops` |
| `PLUGIN_NAME` | Yes | Name of the plugin being executed |
| `PLUGIN_COMMAND` | Yes | Fully interpolated command passed to Claude Code |
| `LLM_API_KEY` | Yes | API key for Claude / LLM provider |
| `GITHUB_TOKEN` | Conditional | Required when `PLATFORM=github` |
| `AZURE_DEVOPS_TOKEN` | Conditional | Required when `PLATFORM=azuredevops` |
| `GIT_REF` | No | Branch or PR ref to check out after clone/fetch |

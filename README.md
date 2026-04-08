# The Xianix Agent

## Architecture

The system connects your code platform to AI-powered automation through a layered pipeline:

```mermaid
flowchart LR
    A["GitHub / Azure DevOps\n(PR events, comments)"]
    B["Xians Webhooks\n(app.xians.ai)"]
    C["TheAgent\n(.NET Console App)"]
    D["Executor\n(Docker container)"]
    E["ClaudeCode Plugins\n(review, triage…)"]

    A -->|webhook| B
    B -->|dispatches task| C
    C -->|spawns & mounts socket| D
    D -->|runs| E
```

| Layer | What it does |
|---|---|
| **GitHub / Azure DevOps** | Source of events — PR opened, comment posted, etc. |
| **Xians Webhooks** | Receives platform events and routes them to the registered agent. |
| **TheAgent** | Long-running .NET process that polls Xians, interprets tasks, and orchestrates execution. |
| **Executor** | Isolated Docker container spawned per task; has no persistent state. |
| **ClaudeCode Plugins** | Skills (e.g. PR review, post-review) that run inside the Executor and call the LLM. |

---

## Setup

1. Copy `.env.example` to `.env` and fill in all required values:

```bash
cp .env.example .env
```

Key variables to set: `XIANS_SERVER_URL`, `XIANS_API_KEY`, `LLM_API_KEY`, and at least one platform token (`GITHUB_TOKEN` or `AZURE_DEVOPS_TOKEN`). See `.env.example` for the full list.

## Docker Images

The project produces two Docker images, both published to Docker Hub under the `99xio` org.

### Executor

The agent spawns executor containers to run plugins in isolated environments. See [Executor/README.md](Executor/README.md) for full details.

```bash
cd Executor/
docker build -t 99xio/xianix-executor:latest .
```

### TheAgent

The agent itself is also containerised. It requires access to the Docker socket to manage executor containers.

```bash
cd TheAgent/
docker build -t 99xio/xianix-agent:latest .
```

Run locally with Docker socket mounted:

```bash
cd TheAgent/
docker run --rm \
  --env-file .env \
  99xio/xianix-agent:latest
```

### Publishing

Both images are published to Docker Hub automatically when you push a version tag:

```bash
VERSION=v1.0.0
git tag $VERSION
git push origin $VERSION
```

This triggers [publish-executor.yml](.github/workflows/publish-executor.yml) and [publish-theagent.yml](.github/workflows/publish-theagent.yml) in parallel. Tags are derived from the version (e.g. `v1.2.3` produces `1.2.3`, `1.2`, `1`, and `latest`). Pre-release tags (e.g. `v1.0.0-rc1`) skip the `latest` tag. Both workflows can also be triggered manually via `workflow_dispatch`.

### CI Secrets

Both workflows require a `DOCKERHUB_TOKEN` secret in the repository settings (**Settings > Secrets and variables > Actions**).

## Run

From the repo root:

```bash
dotnet run --project TheAgent/TheAgent.csproj
```

## Tests

```bash
dotnet test TheAgent.Tests/TheAgent.Tests.csproj
```

## Simulating webhooks

Once the agent is running, you can fire simulated GitHub webhook events using the scripts in [`Scripts/`](Scripts/README.md):

```bash
export WEBHOOK_URL=https://app.xians.ai/webhooks/<your-agent-id>
./Scripts/simulate-pr-opened.sh    # should respond { "status": "success" }
./Scripts/simulate-pr-closed.sh    # should respond { "status": "ignored" }
```

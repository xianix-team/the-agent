# the-agent

## Setup

1. Copy `.env.example` to `.env` and fill in all required values:

```bash
cp .env.example .env
```

Key variables to set: `XIANS_SERVER_URL`, `XIANS_API_KEY`, `LLM_API_KEY`, and at least one platform token (`GITHUB_TOKEN` or `AZURE_DEVOPS_TOKEN`). See `.env.example` for the full list.

## Build the Executor image

The agent spawns Docker containers to execute plugins in isolated environments. The executor image must be built before running the agent. See [Executor/README.md](Executor/README.md) for full details.

```bash
cd Executor/
docker build -t xianix-executor:latest .
```

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

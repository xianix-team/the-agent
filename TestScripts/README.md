# Scripts — Webhook Simulation

Manual smoke-test scripts for sending simulated webhook payloads to the agent's Xians webhook endpoint. Useful for verifying end-to-end behaviour without needing a real GitHub or Azure DevOps event.

## Prerequisites

- `curl` (available on macOS/Linux by default)
- `jq` for pretty-printing the response (`brew install jq` / `apt install jq`)
- The agent must be running and connected to the Xians platform
- `WEBHOOK_URL` — the Xians webhook endpoint URL for your agent (visible in the Xians dashboard)

## Scripts

| Script | Simulates | Expected response |
|--------|-----------|-------------------|
| `simulate-pr-opened.sh` | GitHub `pull_request` with `action=opened` | `{ "status": "success" }` — rule matches, workflow signalled |
| `simulate-pr-closed.sh` | GitHub `pull_request` with `action=closed` | `{ "status": "ignored" }` — filter fails (rule only matches `opened`) |
| `simulate-unknown-webhook.sh` | An unrecognised event type | `{ "status": "ignored" }` — no matching rule |

## Usage

Pass the webhook URL as the first argument, or export it as `WEBHOOK_URL`:

```bash
# Via env var (recommended for repeated use)
export WEBHOOK_URL=https://app.xians.ai/webhooks/<your-agent-id>

./simulate-pr-opened.sh
./simulate-pr-closed.sh
./simulate-unknown-webhook.sh
```

```bash
# Or as a positional argument
./simulate-pr-opened.sh https://app.xians.ai/webhooks/<your-agent-id> 42 https://github.com/org/repo
#                       ^webhook-url                                   ^pr-number ^repo-url
```

## PR opened — full example

```bash
export WEBHOOK_URL=https://app.xians.ai/webhooks/abc123
export PR_NUMBER=42
export REPO_URL=https://github.com/my-org/my-repo

./simulate-pr-opened.sh
```

Expected response:
```json
{
  "status": "success",
  "inputs": {
    "pr-number": 42,
    "repository-url": "https://github.com/my-org/my-repo",
    "platform": "github"
  }
}
```

After the response comes back, the `ActivationWorkflow` will receive a signal, start a `ProcessingWorkflow`, and spin up an executor container that clones the repo and runs the `pr-review` plugin.

## What to watch for

- **Agent logs** (`dotnet run`) — look for `"Webhook 'pull requests' matched"` and then container lifecycle messages.
- **Docker** — `docker ps` to see the executor container while it runs; `docker volume ls` to confirm the workspace volume was created.

# End-to-End Tests — TheAgent on local Xians ACP

Black-box tests for the **full integration chain** of a deployed agent:

```
simulated webhook → Xians ACP → TheAgent (Integrator Workflow)
    → rules.json evaluation → ProcessingWorkflow
    → executor container (Docker) → Claude Code → result envelope
```

The harness sends GitHub- and Azure-DevOps-shaped webhook payloads to the
platform webhook endpoint and verifies every hop by observing the HTTP
response, the Docker containers the agent starts, and the JSON envelope the
executor writes to stdout.

## How the chain is proven

A tiny git repository (the *fixture*) is generated fresh for every run with a
random token planted in `TOKEN.txt`, and served to the executor container via
`git daemon` on `git://host.docker.internal:<port>/...`. The `e2e-*` rules
instruct Claude to read `TOKEN.txt` and echo its contents. The token showing
up in the result envelope is only possible if the webhook was routed, the rule
matched, the workflow ran, the container started, the repo was cloned, and
Claude Code actually executed — the whole chain, no mocks.

Because the rules resolve `repository.url` from the payload
(`repository.clone_url` / `resource.repository.remoteUrl`), the harness steers
the executor at the local fixture purely through the simulated payload — no
GitHub/ADO account, repo, or token is involved. The `e2e-*` rules declare
`"platform": "local"` so the executor clones without credentials.

## Prerequisites

1. **Local Xians ACP running** and reachable (e.g. `http://localhost:5005`).
2. **TheAgent deployed and registered** — running with activation-scoped Rules that
   include the two `e2e-*` execution blocks. The system seed
   [`TheAgent/Knowledge/rules.json`](../../TheAgent/Knowledge/rules.json) is intentionally
   empty; for Tier 2 tests upload
   [`TheAgent.Tests/Fixtures/e2e-rules.json`](../../TheAgent.Tests/Fixtures/e2e-rules.json)
   to activation-scoped Knowledge (`Rules`) before running, or merge those executions into
   your activation rules in Studio. **Restart the agent after any rules change** or the
   platform still has the old rules.
3. **Docker** on this machine (the agent starts executor containers on the
   same daemon, which is what lets the harness observe them).
4. **`ANTHROPIC-API-KEY`** configured for the agent (host `.env` or rules
   `with-envs`) — the Tier 2 tests run a real (tiny, Haiku, budget-capped)
   Claude session.
5. `jq`, `curl`, `git` on this machine.

## Setup

```bash
cp tests/e2e/.env.example tests/e2e/.env
# edit tests/e2e/.env — paste your WEBHOOK_URL from Xians Studio
```

`.env` is gitignored (it contains your API key ID).

## Run

```bash
cd tests/e2e
./e2e_test.sh
```

Options (env vars):

| Variable | Default | Purpose |
|----------|---------|---------|
| `WEBHOOK_URL` | — (required) | Full webhook endpoint incl. query string |
| `SKIP_TIER2` | `0` | `1` = routing tests only, no Claude cost |
| `FIXTURE_PORT` | `9418` | Port for the local `git daemon` |
| `FIXTURE_HOST` | `host.docker.internal` | How the executor container reaches this machine (Linux: use the host IP) |
| `CONTAINER_START_TIMEOUT` | `90` | Seconds to wait for the executor container to appear |
| `CONTAINER_EXIT_TIMEOUT` | `420` | Seconds to wait for the Claude run to finish |
| `KEEP_ARTIFACTS` | `0` | `1` = keep the test containers/volumes for inspection |

## What runs

| # | Tier | Test | Verifies |
|---|------|------|----------|
| 1 | 1 (free) | Unrecognised event | Answered `status=ignored`; no container starts |
| 2 | 1 (free) | PR without the `e2e/echo` label | `match-any` filter miss → `ignored`; no container |
| 3 | 2 (~$0.01) | GitHub `pull_request opened` with `e2e/echo` label | `status=success` response; identical redelivery suppressed by the dedup guard; executor container starts, exits 0; envelope `status=completed`; planted token echoed; labelled workspace volume exists |
| 4 | 2 (~$0.01) | Azure DevOps `git.pullrequest.created` with `e2e/echo` label | Same full-chain assertions for the ADO payload shape |

Total cost per full run is about two cents (Haiku, `max-turns: 8`,
`max-budget-usd: 0.25` per block, enforced by the rules themselves).

## Troubleshooting

- **`status` never `success` in Test 3/4** — the agent is probably running
  with old rules. Rebuild + restart the agent so the `e2e-*` blocks upload.
- **Container never appears** — check the agent logs for the
  `ProcessingWorkflow` start; confirm `EXECUTOR-IMAGE` points at an image
  that exists locally.
- **Clone fails inside the container** — the container can't reach the
  `git daemon`. On Linux set `FIXTURE_HOST` to your host's IP; also check
  the port isn't firewalled and nothing else holds `FIXTURE_PORT`.
- **Envelope `status=error` with an Anthropic auth error** — the agent host
  has no (valid) `ANTHROPIC-API-KEY`.

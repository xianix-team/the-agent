# Tenant Isolation Design — Docker Container per Execution

## 1. Problem Statement

The current architecture has a skeleton `TenantWorkspaceActivities` that logs but performs no real isolation. The `ActivationWorkflow` passes `TenantId = string.Empty` into workspace initialization, and all agent logic runs in the same host process. This means:

- **No filesystem isolation** — tenant repos would share the host filesystem.
- **No process isolation** — a misbehaving plugin in one tenant could crash or compromise another.
- **No resource isolation** — one tenant's workload could starve others of CPU/memory.
- **Credential leakage risk** — secrets (API keys, tokens) available to one tenant could be accessed by another.

For a multi-tenant agent platform, these are unacceptable. We need strong isolation boundaries per tenant execution.

## 2. Design Goals

| Goal | Description |
|------|-------------|
| **Security isolation** | Each tenant execution runs in its own container with no access to other tenants' data, credentials, or processes. |
| **Filesystem isolation** | Each tenant+repo pair gets its own persistent volume. No tenant can access another's volume. |
| **Resource limits** | CPU, memory, and disk can be capped per container to prevent noisy-neighbour effects. |
| **Credential scoping** | Only the credentials required for a specific tenant/repo are injected into that container. |
| **Ephemeral containers, persistent workspaces** | Containers are short-lived — created per processing event, destroyed after completion. But the workspace volume (cloned repo) persists across requests to avoid redundant cloning. |
| **Public network access** | Containers have full outbound internet access. Agents may need to reach package registries, APIs, documentation sites, etc. Isolation is at the container/tenant boundary, not the network boundary. |
| **Plugin execution via Claude Code SDK** | Inside the container, the Claude Code SDK is invoked with full tool access — the container itself is the security boundary, not tool restrictions. |

## 3. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│  Host: .NET Agent (Control Plane)                                   │
│                                                                     │
│  ┌──────────────┐  ┌────────────────────┐  ┌────────────────────┐  │
│  │ XianixAgent   │  │ ActivationWorkflow │  │ EventOrchestrator  │  │
│  │ (Xians ACP)   │──│ (Temporal)         │──│ (Rules Engine)     │  │
│  └──────────────┘  └────────────────────┘  └────────────────────┘  │
│                            │                                        │
│                    ┌───────▼────────┐                               │
│                    │ ProcessingWF   │                               │
│                    │ (Temporal)     │                               │
│                    └───────┬────────┘                               │
│                            │                                        │
│                    ┌───────▼──────────────┐                         │
│                    │ ContainerActivities  │                         │
│                    │ (Docker Management)  │                         │
│                    └───────┬──────────────┘                         │
│                            │ Docker API                             │
├────────────────────────────┼────────────────────────────────────────┤
│            Docker Engine   │                                        │
│                            │                                        │
│   Persistent Volumes (per tenant+repo):                             │
│   ┌───────────────────────────────┐                                 │
│   │  vol: tenant-abc_repo-xyz     │  ← survives container restarts  │
│   │  /workspace/repo/  (cloned)   │                                 │
│   └──────────────┬────────────────┘                                 │
│                  │ mounted into                                      │
│   ┌──────────────▼────────────────────────────────────────┐         │
│   │  Tenant Container (ephemeral)                          │         │
│   │                                                        │         │
│   │  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │         │
│   │  │ git fetch /   │→ │ Claude Code  │→ │ Plugin Exec │ │         │
│   │  │ clone (smart) │  │ SDK (Python) │  │ (pr-review) │ │         │
│   │  └──────────────┘  └──────────────┘  └─────────────┘ │         │
│   │                                                        │         │
│   │  Full internet access (public network)                 │         │
│   └────────────────────────────────────────────────────────┘         │
│                                                                      │
│   ┌──────────────────────────────────────────────────────────┐       │
│   │  Another Tenant Container (parallel, different volume)    │       │
│   │  ...                                                      │       │
│   └──────────────────────────────────────────────────────────┘       │
└──────────────────────────────────────────────────────────────────────┘
```

The .NET agent remains the **control plane** — it receives webhooks, evaluates rules, manages Temporal workflows, and orchestrates container lifecycles via the Docker API. It never clones repos or runs plugins on the host. All tenant-specific execution happens inside isolated Docker containers.

**Key design principles:**

- **Containers are ephemeral** — spun up per event, torn down after completion. No long-running containers.
- **Workspace volumes are persistent** — each tenant+repo combination gets a named Docker volume. The cloned repository survives container restarts, so subsequent requests do a fast `git fetch + checkout` instead of a full clone.
- **Full internet access** — containers have unrestricted outbound networking. The isolation boundary is the container itself (filesystem, process, credentials), not the network.
- **Full tool access** — the Claude Code SDK is invoked without artificial tool restrictions. The container is the sandbox; everything inside it is fair game for the agent.

## 4. Solution Structure

The repo becomes a polyglot monorepo with two deployable artifacts: the .NET control plane and the Python-based executor image.

```
the-agent/
├── .env.example                    # Shared env template (all secrets)
├── .gitignore
├── .vscode/
│   ├── launch.json
│   └── tasks.json
├── global.json                     # .NET SDK pin
├── README.md
├── LICENSE
│
├── Docs/                           # Architecture & design docs
│   ├── architecture.md
│   ├── architecture-diagrams.html
│   └── tenant-isolation-design.md
│
├── TheAgent/                       # .NET control plane (existing)
│   ├── TheAgent.csproj
│   ├── Program.cs
│   ├── Constants.cs
│   ├── EnvConfig.cs
│   ├── Agent/
│   │   ├── XianixAgent.cs
│   │   ├── MafSubAgent.cs
│   │   ├── MafSubAgentTools.cs
│   │   └── ChatHistoryProvider.cs
│   ├── Workflows/
│   │   ├── ActivationWorkflow.cs
│   │   └── ProcessingWorkflow.cs
│   ├── Activities/
│   │   └── ContainerActivities.cs  # NEW — replaces TenantWorkspaceActivities
│   ├── Orchestrator/
│   │   ├── IEventOrchestrator.cs
│   │   ├── EventOrchestrator.cs
│   │   └── OrchestrationResult.cs
│   ├── Rules/
│   │   ├── IWebhookRulesEvaluator.cs
│   │   ├── WebhookRulesEvaluator.cs
│   │   └── WebhookRulesModels.cs
│   └── Knowledge/
│       ├── rules.json
│       └── plugins.json
│
├── TheAgent.Tests/                 # .NET tests (existing)
│   ├── TheAgent.Tests.csproj
│   └── Orchestrator/
│       └── EventOrchestratorTests.cs
│
└── Executor/                       # NEW — Docker executor image
    ├── Dockerfile
    ├── entrypoint.sh               # Clone-or-fetch + launch plugin
    ├── execute_plugin.py           # Claude Code SDK invocation
    ├── requirements.txt            # Python dependencies (claude-code-sdk, etc.)
    └── README.md                   # Build & run instructions for the image
```

### Why a root-level `Executor/` folder

| Concern | Reasoning |
|---------|-----------|
| **Different runtime** | The executor is Python; the control plane is .NET. They share nothing at build time. |
| **Separate build artifact** | `TheAgent/` produces a .NET console app. `Executor/` produces a Docker image. Different CI pipelines. |
| **Independent versioning** | The image can be tagged and released on its own cadence (e.g., when the SDK or base image updates). |
| **Clear ownership boundary** | Everything Docker-related is in one place. The .NET project references the image by name/tag, never by file path. |
| **Convention** | Follows the standard polyglot monorepo pattern (e.g., `backend/`, `frontend/`, `infra/`). |

### How the two artifacts connect

The .NET control plane (`TheAgent/`) references the executor image **by name only** — it never touches the `Executor/` folder at runtime. The connection is:

```
TheAgent/Activities/ContainerActivities.cs:
    const string ExecutorImage = "xianix-executor:latest";
    // Docker.DotNet uses this image name to create containers
```

The `Executor/` image is built separately (CI or local `docker build`) and pushed to a container registry. The control plane pulls it from there.

```bash
# Build the executor image
cd Executor/
docker build -t xianix-executor:latest .

# The .NET control plane references "xianix-executor:latest"
cd ../TheAgent/
dotnet run
```

## 5. Container Image Design

### 5.1 Base Image: `xianix-executor`

A pre-built Docker image that contains everything needed to execute plugins on a codebase. It does **not** contain any tenant-specific data — that is injected at runtime.

```dockerfile
FROM python:3.12-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    && rm -rf /var/lib/apt/lists/*

# Install Claude Code SDK
RUN pip install --no-cache-dir claude-code-sdk

# Install any additional language runtimes needed for plugin execution
# (Node.js for JS repos, dotnet-sdk for C# repos, etc. — or use multi-stage variants)

WORKDIR /workspace

# Entrypoint script that orchestrates: clone → plugin install → plugin exec → output
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
```

### 5.2 Entrypoint Script

The entrypoint is parameterized via environment variables. It handles both first-run (clone) and subsequent runs (fetch) against the persistent workspace volume. All progress logging goes to **stderr** to keep **stdout** clean for the structured JSON result:

```bash
#!/usr/bin/env bash
set -euo pipefail

# All progress logging to stderr — stdout is reserved for the JSON result
log() { echo "$@" >&2; }

log "=== Xianix Executor ==="
log "Tenant:     ${TENANT_ID}"
log "Repository: ${REPOSITORY_URL}"
log "Plugin:     ${PLUGIN_NAME}"
log "Command:    ${PLUGIN_COMMAND}"

# Configure git credential based on platform
if [ "${PLATFORM}" = "github" ]; then
    git config --global url."https://${GITHUB_TOKEN}@github.com/".insteadOf "https://github.com/"
elif [ "${PLATFORM}" = "azuredevops" ]; then
    git config --global url."https://pat:${AZURE_DEVOPS_TOKEN}@dev.azure.com/".insteadOf "https://dev.azure.com/"
fi

REPO_DIR="/workspace/repo"

# Smart clone: reuse existing repo if present, otherwise fresh clone
if [ -d "${REPO_DIR}/.git" ]; then
    log "--- Repository already cloned, fetching latest ---"
    cd "${REPO_DIR}"
    if ! git fetch --all --prune 2>&2; then
        log "--- Fetch failed, repo may be corrupted. Re-cloning ---"
        cd /workspace && rm -rf "${REPO_DIR}"
        git clone "${REPOSITORY_URL}" "${REPO_DIR}" 2>&2
        cd "${REPO_DIR}"
    else
        git reset --hard origin/HEAD 2>&2
        git clean -fdx 2>&2
    fi
else
    log "--- Cloning repository (first run for this tenant+repo) ---"
    git clone "${REPOSITORY_URL}" "${REPO_DIR}" 2>&2
    cd "${REPO_DIR}"
fi

# If a specific branch/ref is provided (e.g., PR head)
if [ -n "${GIT_REF:-}" ]; then
    log "--- Checking out ref: ${GIT_REF} ---"
    git fetch origin "${GIT_REF}" 2>&2
    git checkout FETCH_HEAD 2>&2
fi

# Execute the plugin via Claude Code SDK
# stdout from this script IS the result — captured by the control plane
log "--- Executing plugin: ${PLUGIN_NAME} ---"
python3 /workspace/execute_plugin.py

log "--- Execution complete ---"
```

### 5.3 Plugin Execution Script (`execute_plugin.py`)

This Python script uses the Claude Code SDK to invoke the appropriate plugin operation on the cloned codebase. The container itself is the security boundary, so the SDK is given full tool access. Results are written to **stdout** as structured JSON for the control plane to capture via the Docker API:

```python
import os
import sys
import json
import asyncio
from claude_code_sdk import Claude

async def main():
    tenant_id = os.environ["TENANT_ID"]
    plugin_name = os.environ["PLUGIN_NAME"]
    plugin_command = os.environ["PLUGIN_COMMAND"]
    repo_path = "/workspace/repo"

    claude = Claude(
        api_key=os.environ["LLM_API_KEY"],
        working_directory=repo_path,
    )

    # No tool restrictions — the container is the sandbox.
    # The agent has full access to read, write, execute, and fetch
    # within this isolated environment.
    result = await claude.run(prompt=plugin_command)

    output = {
        "tenant_id": tenant_id,
        "plugin": plugin_name,
        "status": "completed",
        "result": result,
    }

    # Write structured result to stdout — the control plane reads
    # this via Docker's attach/logs API. All progress/debug logging
    # goes to stderr so it doesn't pollute the result stream.
    json.dump(output, sys.stdout, indent=2)

asyncio.run(main())
```

## 6. Control Plane Integration

### 6.1 New Activities: `ContainerActivities`

Replace the current skeleton `TenantWorkspaceActivities` with a `ContainerActivities` class that manages Docker container lifecycles. This becomes the bridge between the Temporal workflow and the Docker API.

```
Activities/
├── ContainerActivities.cs         # Docker lifecycle management
├── ContainerExecutionInput.cs     # Input model
├── ContainerExecutionResult.cs    # Output model
└── TenantWorkspaceActivities.cs   # Deprecated → remove
```

**Key activities:**

| Activity | Purpose |
|----------|---------|
| `EnsureWorkspaceVolumeAsync` | Create or verify the named Docker volume for this tenant+repo pair. Volume name derived from tenant ID + repo hash. |
| `StartContainerAsync` | Pull image, create & start an ephemeral container with shared secrets (platform-selected) and the persistent workspace volume mounted. Returns container ID. |
| `WaitAndCollectOutputAsync` | Attaches to container stdout/stderr streams. Collects stdout (structured JSON result) and stderr (progress logs) until the container exits. Enforces timeout. Returns exit code + parsed result. |
| `CleanupContainerAsync` | Stops and removes the container. Volume is **not** deleted (persists for next request). |

**Why stdio over file-based collection:**

| Aspect | stdio (Docker attach/logs) | File-based (docker cp) |
|--------|---------------------------|----------------------|
| Streaming | Results stream in real-time as the container runs | Must wait for container to finish, then copy file |
| No extra volume | Output doesn't need to be written to the workspace volume | Needs writable `/workspace/output/` |
| Simpler cleanup | Nothing to clean up — output is in memory | Must ensure result.json is deleted between runs |
| Progress visibility | stderr streams progress logs live to the control plane | Logs only visible via `docker logs` after the fact |
| Protocol | stdout = structured JSON result, stderr = human-readable logs | Single result.json file |

### 6.2 Updated ProcessingWorkflow

The `ProcessingWorkflow` becomes the orchestrator for a single tenant execution:

```
ProcessingWorkflow.WorkflowRun(OrchestrationResult result):
    1. Extract tenant ID, repo URL, plugin info from result.Inputs
    2. EnsureWorkspaceVolumeAsync(tenantId, repoUrl) → volumeName
    3. StartContainerAsync(input, volumeName)          → containerId
    4. WaitAndCollectOutputAsync(containerId, timeout)  → { exitCode, stdout (JSON result), stderr (logs) }
    5. CleanupContainerAsync(containerId)               → (always, container only — volume persists)
    6. Parse stdout as ContainerExecutionResult
    7. Return/report result
```

### 6.3 Updated ActivationWorkflow

The `ActivationWorkflow` changes are minimal:

- Pass the real `TenantId` from `OrchestrationResult` into downstream workflows (fix the current `string.Empty` bug).
- Remove the `InitializeTenantWorkspaceAsync` activity call — workspace init now happens inside the container.
- Continue routing signals to `ProcessingWorkflow` instances as before.

## 7. Credential & Secret Management

Secrets are **shared across all tenants** — there are no per-tenant vaults or scoped credentials. The agent platform manages a single set of secrets that get injected into every container. The key distinction is **platform-specific tokens**: since the agent supports both GitHub and Azure DevOps, the appropriate platform token is selected based on the `platform` input extracted from the webhook event.

### 7.1 Shared Secret Model

```
┌──────────────────────────────────┐       ┌───────────────────────────────┐
│  Agent Host Environment (.env)    │       │  Container Environment         │
│                                   │       │                                │
│  ANTHROPIC-API-KEY=...             │──────▶│  ANTHROPIC_API_KEY=...        │
│  GITHUB-TOKEN=...                 │       │  GITHUB_TOKEN=...  (if GitHub) │
│  AZURE-DEVOPS-TOKEN=...           │       │  AZURE_DEVOPS_TOKEN=... (if AzDO)│
│                                   │       │  PLATFORM=github|azuredevops   │
│  (loaded via EnvConfig)           │       │                                │
│                                   │       │  stdout → JSON result           │
│                                   │       │  stderr → progress logs         │
└──────────────────────────────────┘       └───────────────────────────────┘
```

### 7.2 Secret Inventory

| Secret | Purpose | When Injected |
|--------|---------|---------------|
| `ANTHROPIC-API-KEY` | Claude / LLM API access for the Claude Code SDK | Always |
| `GITHUB-TOKEN` | GitHub API access (clone private repos, post PR comments, update checks) | When `platform == "github"` |
| `AZURE-DEVOPS-TOKEN` | Azure DevOps API access (clone repos, post PR comments, update work items) | When `platform == "azuredevops"` |
| `PLATFORM` | Identifies which CM platform this execution targets | Always (derived from webhook inputs) |

### 7.3 Platform Selection Flow

The `platform` value comes from the `rules.json` input rules — it's already defined as a constant input:

```json
{
    "name": "platform",
    "value": "github",
    "constant": true
}
```

The control plane uses this to decide which platform token to inject:

```
ContainerActivities.StartContainerAsync:
    1. Read shared secrets from host environment (EnvConfig)
    2. Read "platform" from OrchestrationResult.Inputs
    3. Build env var map:
       - Always: ANTHROPIC_API_KEY, PLATFORM, TENANT_ID, REPOSITORY_URL, PLUGIN_NAME, PLUGIN_COMMAND
       - If platform == "github":      GITHUB_TOKEN
       - If platform == "azuredevops": AZURE_DEVOPS_TOKEN
    4. Pass as Docker container env vars at creation time
    5. Secrets exist only in container memory, destroyed on cleanup
```

### 7.4 Git Clone Authentication

The entrypoint script uses the injected platform token for authenticated git operations:

```bash
# Configure git credential based on platform
if [ "${PLATFORM}" = "github" ]; then
    git config --global url."https://${GITHUB_TOKEN}@github.com/".insteadOf "https://github.com/"
elif [ "${PLATFORM}" = "azuredevops" ]; then
    git config --global url."https://pat:${AZURE_DEVOPS_TOKEN}@dev.azure.com/".insteadOf "https://dev.azure.com/"
fi
```

### 7.5 EnvConfig Updates

The host `.env` file (and `EnvConfig.cs`) needs these additions:

```env
# Existing
XIANS-SERVER-URL=...
XIANS-API-KEY=...
ANTHROPIC-API-KEY=...

# Platform tokens (shared across all tenants)
GITHUB-TOKEN=ghp_...
AZURE-DEVOPS-TOKEN=...
```

## 8. Security Boundaries

### 8.1 Container Security Configuration

Each container is created with settings that isolate it from the host and other tenants, while giving the agent full capability within its sandbox:

| Setting | Value | Rationale |
|---------|-------|-----------|
| `NetworkMode` | `bridge` (default) | Full outbound internet access. Agents may need to install packages, call APIs, read docs, etc. |
| `Memory` | `512MB–2GB` (configurable per plan) | Prevent OOM on host. |
| `CpuShares` / `NanoCPUs` | Bounded | Fair scheduling across tenants. |
| `PidsLimit` | `256` | Prevent fork bombs. |
| `SecurityOpt` | `no-new-privileges` | Prevent privilege escalation. |
| `User` | Non-root (`1000:1000`) | Least privilege. |
| `CapDrop` | `ALL` | Drop all Linux capabilities. |
| `Volumes` | Named volume → `/workspace` | Persistent workspace, scoped to tenant+repo. |

### 8.2 Network Access

Containers have **full public internet access**. This is a deliberate design choice:

- Agents need to install dependencies (`npm install`, `pip install`, `dotnet restore`, etc.).
- Agents may need to call external APIs (GitHub API, package registries, documentation sites).
- Claude Code itself may invoke web searches or fetch documentation.
- Restricting network would cripple agent effectiveness for most real-world tasks.

**What the container CANNOT do:**

- Access the Docker socket (no container escape).
- Access the host network namespace (isolated bridge network).
- Reach other tenant containers directly (no inter-container routing).
- Access host filesystem outside its mounted volume.

### 8.3 Filesystem Isolation

```
Host filesystem:
├── Docker volumes (managed by Docker, not directly accessible):
│   ├── xianix-tenant-abc-repo-xyz/     ← tenant A's repo
│   ├── xianix-tenant-def-repo-xyz/     ← tenant B's repo (same repo, different tenant)
│   └── xianix-tenant-abc-repo-uvw/     ← tenant A's other repo
└── No bind mounts to host paths

Container filesystem (what the agent sees):
├── / (read-only rootfs — base image with Python, git, SDK)
├── /workspace/          (persistent named volume, writable)
│   ├── repo/            (cloned repository — persists across requests)
│   └── execute_plugin.py
├── stdout               → JSON result (captured by control plane via Docker attach)
├── stderr               → progress logs (captured by control plane via Docker logs)
└── (no host mounts, no Docker socket, no access to other volumes)
```

**Volume naming convention:** `xianix-{tenantId}-{repoHash}` ensures each tenant+repo pair gets its own isolated persistent storage. No tenant can mount another tenant's volume.

**Volume lifecycle:**

| Event | Action |
|-------|--------|
| First request for a tenant+repo | Create named volume. Container clones repo into it. |
| Subsequent requests | Reuse existing volume. Container does `git fetch` + checkout. |
| Tenant offboarded | Delete the volume (admin/cleanup activity). |
| Repo removed from tenant config | Delete the volume (admin/cleanup activity). |

## 9. Container Lifecycle

```mermaid
sequenceDiagram
    autonumber
    participant WF as ProcessingWorkflow
    participant CA as ContainerActivities
    participant DK as Docker Engine
    participant CT as Tenant Container
    participant VOL as Workspace Volume

    WF->>CA: EnsureWorkspaceVolumeAsync(tenantId, repoUrl)
    CA->>DK: Create volume if not exists
    DK-->>CA: volumeName

    WF->>CA: StartContainerAsync(tenantId, repoUrl, plugin, volumeName)
    CA->>CA: Read shared secrets from EnvConfig + select platform token
    CA->>DK: Create container (image, env, limits, mount volume)
    DK-->>CA: containerId
    CA->>DK: Start container + attach to stdout/stderr
    DK->>CT: Run entrypoint.sh

    CT->>VOL: Check /workspace/repo/.git exists?
    alt First run (no repo)
        CT->>CT: git clone → /workspace/repo/
    else Subsequent run (repo exists)
        CT->>CT: git fetch --all + reset
    end
    CT->>CT: git checkout specific ref (if provided)
    CT-->>CA: stderr: progress logs (streamed live)
    CT->>CT: Execute plugin via Claude Code SDK (full tool access)
    CT-->>CA: stdout: JSON result
    CT-->>CA: stderr: execution logs

    WF->>CA: WaitAndCollectOutputAsync(containerId, timeout)
    CA->>DK: Await container exit, collect stdout + stderr streams
    DK-->>CA: exit code 0, stdout content, stderr content
    CA-->>WF: ContainerExecutionResult (parsed from stdout JSON)

    WF->>CA: CleanupContainerAsync(containerId)
    CA->>DK: Remove container (volume persists)
    DK-->>CA: done

    WF->>WF: Report result upstream
```

### 9.1 Timeout & Failure Handling

| Scenario | Handling |
|----------|----------|
| Container exceeds timeout | `WaitAndCollectOutputAsync` kills the container after the configured deadline. `CleanupContainerAsync` removes the container (volume persists). Workflow reports timeout with any partial stderr output. |
| Plugin execution fails (exit code != 0) | stderr contains error details. stdout may be empty or partial. `CleanupContainerAsync` removes the container. Workflow reports failure with stderr diagnostics. Volume persists (repo state is reset on next run via `git reset --hard`). |
| Docker daemon unavailable | `StartContainerAsync` fails. Temporal retries per `RetryPolicy` (3 attempts, exponential backoff). |
| Container OOM killed | Docker reports OOM exit. Workflow reports resource limit exceeded. |
| Image pull fails | `StartContainerAsync` fails. Temporal retries. Alert after final attempt. |
| Corrupted volume (bad git state) | Entrypoint detects and falls back to fresh clone (delete `/workspace/repo` and re-clone). |
| Malformed stdout JSON | `WaitAndCollectOutputAsync` fails to parse. Workflow reports error with raw stdout + stderr for debugging. |

## 10. Data Flow: End-to-End

```mermaid
flowchart TD
    GH[GitHub / AzDO Webhook] -->|POST| ACP[Xians ACP]
    ACP -->|OnWebhook| WH[WebhookWorkflow]
    WH --> ORCH[EventOrchestrator]
    ORCH --> RULES[RulesEvaluator]
    RULES -->|matched| SIG[Signal ActivationWorkflow]

    SIG --> AWF[ActivationWorkflow]
    AWF -->|Start child| PWF[ProcessingWorkflow]

    PWF --> VOL[EnsureWorkspaceVolumeAsync]
    VOL -->|Create/verify| VOLUME[(Persistent Volume\nper tenant+repo)]

    PWF --> START[ContainerActivities.StartContainerAsync]
    START -->|Read shared secrets + select platform token| ENV[EnvConfig]
    START -->|Create + start + mount volume + attach stdio| DOCKER[Docker Engine]

    DOCKER --> CONTAINER[Tenant Container\nFull internet access]
    CONTAINER -->|First run| CLONE[git clone]
    CONTAINER -->|Subsequent| FETCH[git fetch + reset]
    CLONE & FETCH --> SDK[Claude Code SDK\nFull tool access]
    SDK --> PLUGIN[Plugin Execution]
    PLUGIN -->|stdout: JSON result| STDIO[stdio stream]
    PLUGIN -->|stderr: progress logs| STDIO

    PWF --> WAIT[WaitAndCollectOutputAsync\nParse stdout JSON]
    PWF --> CLEANUP[CleanupContainerAsync\nRemove container, keep volume]

    WAIT --> REPORT[Report to Xians Platform / GitHub]
```

## 11. Rules.json: Plugin Resolution

The current `rules.json` already contains a `plugins` array per rule set, but the C# `WebhookRuleSet` model does not parse it. This needs to be wired up so the orchestrator can pass plugin information into the processing workflow.

**Current `rules.json` structure (already defined):**

```json
{
    "webhook-name": "pull requests",
    "match": [...],
    "inputs": [...],
    "plugins": [
        {
            "name": "pr-review",
            "description": "A plugin for reviewing pull requests",
            "url": "github@claude-plugins-official",
            "command": "/pr-review <pr-number> <repository-url>"
        }
    ]
}
```

**What needs to change:**

1. Add `Plugins` property to `WebhookRuleSet` model.
2. `WebhookRulesEvaluator` includes matched plugins in the evaluation result.
3. `OrchestrationResult.Inputs` carries plugin info (name, command with interpolated values).
4. `ProcessingWorkflow` passes plugin info to `ContainerActivities`.

## 12. Scalability Considerations

### 12.1 Container Pool (Optional Optimization)

For high-throughput scenarios, maintain a warm pool of pre-created containers:

```
┌─────────────────────────────────────────┐
│  Container Pool Manager                  │
│                                          │
│  Warm pool:  [container1] [container2]   │
│  In use:     [container3] [container4]   │
│  Cooldown:   [container5]               │
│                                          │
│  Policy: min=2, max=20, idle-ttl=5min    │
└─────────────────────────────────────────┘
```

Warm containers have the base image ready but no tenant data. On assignment, secrets and repo URL are injected and the entrypoint begins.

### 12.2 Horizontal Scaling

- The .NET agent (control plane) can run as multiple replicas behind Temporal.
- Docker containers can be scheduled across multiple hosts using Docker Swarm or Kubernetes.
- Future: replace raw Docker with Kubernetes Jobs for native scheduling, resource quotas, and namespace isolation.

### 12.3 Resource Quotas per Tenant

| Resource | Enforcement |
|----------|-------------|
| Concurrent containers | Max N containers per tenant (tracked in Temporal workflow state or Redis) |
| CPU/memory per container | Docker `--memory`, `--cpus` flags |
| Execution time | Workflow-level timeout on `WaitForContainerAsync` |
| Volume storage | Max volume size per tenant (monitored, with alerts). Periodic cleanup of stale volumes. |
| API calls | LLM API key quota tracking at the platform level |

## 13. Implementation Phases

### Phase 1: Foundation

- [ ] Build `xianix-executor` Docker image with git, Python, Claude Code SDK.
- [ ] Implement `ContainerActivities` with `EnsureWorkspaceVolumeAsync`, `StartContainerAsync`, `WaitForContainerAsync`, `CollectResultsAsync`, `CleanupContainerAsync`.
- [ ] Wire `ProcessingWorkflow` to use `ContainerActivities` instead of the skeleton.
- [ ] Pass real `TenantId` from `OrchestrationResult` through the workflow chain.
- [ ] Smart `entrypoint.sh` that clones on first run and fetches on subsequent runs.
- [ ] `execute_plugin.py` invoking Claude Code SDK with full tool access.
- [ ] Named Docker volume management (create, mount, naming convention).

### Phase 2: Security Hardening

- [ ] Container security settings (drop caps, no-new-privileges, non-root user).
- [ ] Volume isolation verification (no cross-tenant volume access).
- [ ] Platform-aware git credential injection (GitHub token vs Azure DevOps PAT).
- [ ] Audit logging — log container lifecycle events, secret access, plugin outputs.
- [ ] Volume cleanup tooling for offboarded tenants / removed repos.

### Phase 3: Plugin Framework

- [ ] Parse `plugins` array from `rules.json` in `WebhookRuleSet`.
- [ ] Interpolate input values into plugin commands (e.g., `<pr-number>` → actual PR number).
- [ ] Support multiple plugins per webhook event (sequential or parallel within the container).
- [ ] Plugin result parsing and reporting back to the platform (PR comments, status checks).

### Phase 4: Scalability & Operations

- [ ] Container pool manager for warm starts.
- [ ] Per-tenant concurrency limits.
- [ ] Metrics and monitoring (container duration, success/failure rates, resource usage).
- [ ] Migration path to Kubernetes Jobs for production workloads.
- [ ] Multi-architecture image builds (amd64 + arm64).

## 14. Mapping to Current Codebase

| Current Component | Change |
|-------------------|--------|
| `TenantWorkspaceActivities` | **Replace** with `ContainerActivities`. Remove old skeleton. |
| `ActivationWorkflow` | **Fix** `TenantId = string.Empty` → use `result.TenantId`. Remove `InitializeTenantWorkspaceAsync` call (workspace init moves into container). |
| `ProcessingWorkflow` | **Implement** container lifecycle: start → wait → collect → cleanup. |
| `OrchestrationResult` | **Extend** with plugin information from rules evaluation. |
| `WebhookRuleSet` | **Add** `Plugins` property to model. |
| `WebhookRulesEvaluator` | **Include** matched plugins in evaluation output. |
| `rules.json` | **No change** — plugin structure already defined. |
| `Program.cs` | **Register** `ContainerActivities` + Docker client in DI. |
| `EnvConfig` | **Add** `GITHUB-TOKEN`, `AZURE-DEVOPS-TOKEN`, Docker host URL, container resource limits. |

## 15. Technology Stack

| Component | Technology | Rationale |
|-----------|------------|-----------|
| Container runtime | Docker Engine (Linux containers) | Widely available, mature API, security features. |
| Docker client (.NET) | `Docker.DotNet` NuGet package | Official .NET Docker API client. |
| Container image base | `python:3.12-slim` | Minimal Python image for Claude Code SDK. |
| Claude Code SDK | `claude-code-sdk` (Python) | Official SDK for programmatic Claude Code invocation. |
| Secret management | Host `.env` via `EnvConfig` | Shared secrets loaded at agent startup, injected into containers at creation time. |
| CM platforms | GitHub + Azure DevOps | Platform-specific tokens selected per webhook event based on `platform` input. |
| Workflow engine | Temporal (via Xians) | Already in use. Container activities fit naturally as Temporal activities. |
| Future orchestration | Kubernetes Jobs | For production-grade scheduling and multi-node scaling. |

## 16. Open Questions

1. **Multi-language repos**: Should we build variant images (e.g., `xianix-executor-node`, `xianix-executor-dotnet`) or one fat image with multiple runtimes?
2. **Plugin marketplace**: How are plugins discovered and installed? Pre-baked in the image, or fetched at runtime?
3. **Volume size limits**: Docker named volumes don't have native size caps. Do we need a monitoring/enforcement sidecar, or is periodic cleanup sufficient?
4. **Concurrent access to volumes**: If two webhook events for the same tenant+repo arrive simultaneously, should we queue them (one container at a time per volume) or support parallel containers with separate volumes per execution?
5. **Volume garbage collection**: What's the TTL for unused volumes? Should we proactively prune volumes for repos that haven't had activity in N days?
6. **Windows containers**: Any tenants needing Windows-specific toolchains?

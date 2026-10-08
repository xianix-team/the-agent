# Repository Telemetry: the-agent

_Generated from repository contents only._

## 1. Overview
"The Xianix Agent": a .NET console app (`TheAgent`) that receives GitHub / Azure DevOps events via Xians webhooks, interprets them against rules, and spawns an isolated Docker "Executor" container per task that runs Claude Code plugins (review, triage, etc.) (README.md). A second executor image, `Executor.OpenCode`, is also present (Executor.OpenCode/README.md, Executor.OpenCode/Dockerfile).

## 2. Repository shape
Multi-project repository with a solution file (the-agent.sln); no workspace tool declarations (`workspaces`, `pnpm-workspace.yaml`, `nx.json`, etc.) found. Treated as a monorepo of independent projects, identified by separate manifests/Dockerfiles in separate directories.

| Name | Path | Language | Purpose | Evidence |
|---|---|---|---|---|
| TheAgent | `TheAgent/` | C# (.NET) | Long-running .NET process that polls Xians, interprets tasks, orchestrates execution | TheAgent/TheAgent.csproj, README.md |
| TheAgent.Tests | `TheAgent.Tests/` | C# | xUnit tests; references TheAgent | TheAgent.Tests/TheAgent.Tests.csproj |
| Executor | `Executor/` | Python, Bash | Docker image run per task; runs Claude Code plugins | Executor/Dockerfile, Executor/requirements.txt, Executor/README.md |
| Executor.OpenCode | `Executor.OpenCode/` | Python, Bash | Docker image variant installing `opencode-ai` | Executor.OpenCode/Dockerfile, Executor.OpenCode/README.md |
| e2e tests | `tests/e2e/` | Bash | End-to-end harness: simulated webhook → Xians ACP → TheAgent → executor | tests/e2e/README.md, tests/e2e/e2e_test.sh |
| Scripts / TestScripts / Docs | `Scripts/`, `TestScripts/`, `Docs/` | Bash, JSON, Markdown | Deployment scripts, webhook simulation samples, design docs | directory listing |

Shared tooling at root: the-agent.sln, global.json, .gitattributes, .gitignore, .github/workflows/, .vscode/.

## 3. Technologies
**Root**
- .NET SDK `10.0.201`, `rollForward: latestFeature` (global.json).

**TheAgent**
- .NET, `TargetFramework` `net10.0`, `OutputType` Exe, Nullable and ImplicitUsings enabled (TheAgent/TheAgent.csproj).
- Docker build: `mcr.microsoft.com/dotnet/sdk:10.0` (build stage), `mcr.microsoft.com/dotnet/runtime:10.0` (runtime), entrypoint `dotnet TheAgent.dll` (TheAgent/Dockerfile).
- Agent framework packages: `Microsoft.Agents.AI.*`, `Xians.Lib` (TheAgent/TheAgent.csproj). Workflow attributes `[WorkflowRun]` used (TheAgent/Workflows/*.cs).

**TheAgent.Tests**
- `net10.0`, xUnit (`Using Include="Xunit"`), NSubstitute, coverlet.collector (TheAgent.Tests/TheAgent.Tests.csproj).

**Executor**
- Base image `python:3.12-slim-bookworm`; Node 20 (nodesource `node_20.x`), git, curl, jq, universal-ctags, libicu72, GitHub CLI, Azure CLI with azure-devops extension; runs as user `xianix`; `ENTRYPOINT ["/entrypoint.sh"]` (Executor/Dockerfile).
- Installs via pip: `requirements.txt`, plus `semgrep bandit pip-audit` (Executor/Dockerfile). Mentions trivy (Executor/Dockerfile comments).
- Python deps: `claude-agent-sdk==0.2.110`, `playwright==1.49.0` (Executor/requirements.txt).

**Executor.OpenCode**
- Base image `python:3.12-slim-bookworm`; `ARG OPENCODE_VERSION=1.18.31` installed via `npm install -g opencode-ai`; `ARG MISE_VERSION=v2026.8.0` (Executor.OpenCode/Dockerfile).
- No `requirements.txt`: Not found in repository.

**Shell**: `*.sh` forced to `eol=lf` (.gitattributes).

## 4. Libraries and dependencies
**TheAgent** (TheAgent/TheAgent.csproj) — all `PackageReference`, no dev/runtime split declared:
`Azure.AI.OpenAI 2.9.0-beta.1`, `Azure.AI.Projects 2.0.0`, `Azure.Identity 1.21.0`, `CronExpressionDescriptor 2.51.0`, `Docker.DotNet 3.125.15`, `DotNetEnv 3.1.1`, `Microsoft.Agents.AI.Anthropic 1.0.0-rc5`, `Microsoft.Agents.AI.Foundry 1.1.0`, `Microsoft.Agents.AI.OpenAI 1.0.0-rc5`, `OpenTelemetry.Api 1.17.0`, `Xians.Lib 3.36.0`.
A commented-out `ProjectReference` to a local platform repo exists (TheAgent/TheAgent.csproj).
Embedded resources: `**\*.json`, `**\*.md`; `InternalsVisibleTo TheAgent.Tests`.

**TheAgent.Tests** (TheAgent.Tests/TheAgent.Tests.csproj): `coverlet.collector 6.0.4`, `Microsoft.NET.Test.Sdk 17.14.1`, `NSubstitute 5.3.0`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.4`. Internal: `ProjectReference` → `TheAgent`.

**Executor** (Executor/requirements.txt): `claude-agent-sdk==0.2.110`, `playwright==1.49.0`.

**Executor.OpenCode**: `opencode-ai@1.18.31` (Executor.OpenCode/Dockerfile). Python requirements file: Not found in repository.

## 5. Folder structure
```
.
├── .claude/               skill definitions (untracked at time of scan)
├── .github/workflows/     publish-executor.yml, publish-theagent.yml
├── .vscode/               launch.json, tasks.json
├── Docs/                  8 files: architecture, azure-deployment, azure-infrastructure, rules-json, tenant-isolation-design, webhook-headers-guide, ai-dlc-ping-pong (.md), architecture-diagrams.html
├── Executor/              Dockerfile, shell scripts (entrypoint, prepare_repo, run_prompt, generate_context, provision_runtimes, maintain_volume, _common), execute_plugin.py, host_context.py, requirements.txt, tests/
├── Executor.OpenCode/     Dockerfile, shell scripts, execute_opencode.py, plugin_runtime.py, host_context.py, scripts/publish_plugins.py, tests/
├── Scripts/               deploy.sh, vm/ (delete-envs.sh, set-env.sh, start-agent.sh, xianix-agent.service)
├── TestScripts/           simulate-pr-opened.sh, sample webhook JSON payloads, rules-with-schedule.json, README.md
├── TheAgent/              Activities/ (6), Agent/ (6), Containers/ (4), Knowledge/ (3), Orchestrator/ (4), Rules/ (12 incl. Schedule/), Utills/ (1), Workflows/ (14), Program.cs, EnvConfig.cs, Constants.cs, Dockerfile, .env.example, TheAgent.csproj, .vscode/
├── TheAgent.Tests/        Activities/, Containers/, Orchestrator/, Rules/, Workflows/, UnitTest1.cs
├── tests/e2e/             e2e_test.sh, README.md, .env.example
├── global.json, the-agent.sln, README.md, LICENSE, .gitignore, .gitattributes
```

## 6. Testing
- **Unit tests (.NET):** xUnit project `TheAgent.Tests/` (12 test files: Activities 1, Containers 1, Orchestrator 1, Rules 6, Workflows 2, `UnitTest1.cs` with an empty `Test1`). Command documented: `dotnet test TheAgent.Tests/TheAgent.Tests.csproj` (README.md).
- **Executor Python tests:** `Executor/tests/test_host_context.py`, `Executor.OpenCode/tests/test_host_context_open.py` use `unittest`; documented command `python -m unittest discover -s tests -v` (Executor.OpenCode/README.md).
- **Executor integration tests:** `Executor/tests/integration_test.sh`, `Executor.OpenCode/tests/integration_test.sh` — black-box tests against the built image (Executor/README.md).
- **End-to-end tests:** `tests/e2e/e2e_test.sh` — simulated GitHub/Azure DevOps webhooks through Xians ACP, TheAgent and executor container (tests/e2e/README.md).
- **Shell lint in test flow:** `shellcheck --severity=warning Executor/*.sh Executor/tests/*.sh` (.github/workflows/publish-executor.yml).
- Coverage collector: `coverlet.collector` referenced (TheAgent.Tests/TheAgent.Tests.csproj).

## 7. Standards and tooling
- Linter: shellcheck on Executor scripts only, run in CI (.github/workflows/publish-executor.yml). Other linters (ESLint, Ruff, Flake8, Pylint, golangci-lint, RuboCop, Checkstyle, etc.): Not found in repository.
- Formatter config (Prettier, Black, Biome, rustfmt, etc.): Not found in repository.
- Type checker config (mypy): Not found in repository.
- `.editorconfig`: Not found in repository.
- pre-commit config: Not found in repository.
- commit-lint: Not found in repository.
- `CODEOWNERS`: Not found in repository.
- Line endings: `*.sh text eol=lf` (.gitattributes).
- Nullable reference types enabled (TheAgent/TheAgent.csproj, TheAgent.Tests/TheAgent.Tests.csproj).
- Contributor docs: `CLAUDE.md`, `AGENTS.md`, `.cursorrules`, `CONTRIBUTING.md`: Not found in repository. `Docs/` present (8 files); READMEs: README.md, Executor/README.md, Executor.OpenCode/README.md, TestScripts/README.md, tests/e2e/README.md.

## 8. Path classification

### Ignore
| Sub-category | Paths | Tracked / ignored | Evidence |
|---|---|---|---|
| Vendored / third-party | `Executor.OpenCode/vendor/` (ignored, runtime plugin dir `XIANIX_PLUGINS_DIR=/workspace/vendor/plugins`) | ignored | Executor.OpenCode/.gitignore, Executor.OpenCode/Dockerfile. No tracked vendored code: Not found in repository |
| Committed build output | Not found in repository (`bin/`, `obj/`, `[Dd]ebug/`, `[Rr]elease/`, `artifacts/`, `*.nupkg` are git-ignored) | ignored | .gitignore |
| Lockfiles | Not found in repository (`project.lock.json`, `project.fragment.lock.json` git-ignored; no `packages.lock.json`, `package-lock.json`, `*.lock` tracked) | ignored | .gitignore, `git ls-files` |
| Generated code | Not found in repository. Content search for "auto-generated / DO NOT EDIT / `<auto-generated>`" matched only strings emitted at runtime (Executor/generate_context.sh:205, Executor/provision_runtimes.sh:192, and the Executor.OpenCode equivalents) and prose in Docs/azure-infrastructure.md:307, TheAgent/.env.example:25, TheAgent/EnvConfig.cs:144 — none are generated source files | n/a | `git grep` |
| Snapshots / data dumps | Not found in repository | n/a | `git ls-files` |
| Git-ignored local artefacts | `.env`, `.local/`, `id_rsa`, `TheAgent/.env.production`, `TestResult.xml`, `nunit-*.xml`, test result dirs, `__pycache__/`, `*.py[cod]`, `*.lscache`; `Executor.OpenCode/`: `vendor/`, `*.pyc`, `.pytest_cache/`, `tests/.env` | ignored | .gitignore, Executor.OpenCode/.gitignore |

### High scrutiny
**TheAgent**
| Sub-category | Path | Reason / evidence / repo facts | Tests |
|---|---|---|---|
| Authentication & authorisation | `TheAgent/Rules/WebhookVerificationGate.cs`; secrets via `TheAgent/Utills/EnvResolver.cs`, `TheAgent/Rules/StartupEnvResolver.cs` | GitHub HMAC-SHA256 over raw payload against `X-Hub-Signature-256`; Azure DevOps shared header; secret fetched from tenant Secret Vault; failed check skips orchestration (WebhookVerificationGate.cs, Docs/webhook-headers-guide.md). `rules.json` values must be `host.VAR`, `secrets.KEY`, or a constant literal (Docs/rules-json.md). Tenant context in `TheAgent/Activities/TenantRepository.cs`, `TheAgent/Containers/TenantVolumeReader.cs` | `TheAgent.Tests/Rules/WebhookVerificationGateTests.cs`, `StartupEnvResolverTests.cs`, `RulesEnvCatalogTests.cs` |
| Payments & billing | Not found in repository (`TheAgent/Workflows/ModelPricing.cs`, `ExecutionCostResolver.cs` compute model/execution cost, not payments) | — | — |
| Infrastructure & CI/CD | `TheAgent/Dockerfile`, `Executor/Dockerfile`, `Executor.OpenCode/Dockerfile`, `.github/workflows/publish-executor.yml`, `publish-theagent.yml`, `Scripts/deploy.sh`, `Scripts/vm/` (incl. `xianix-agent.service`) | Docker socket `/var/run/docker.sock` mounted into the agent container (Scripts/deploy.sh:99, Scripts/vm/start-agent.sh:61); Executor runs as non-root user `xianix` (Executor/Dockerfile:168); workflows use DockerHub secret `DOCKERHUB_TOKEN`; `publish-executor.yml` runs on tags `v*`, PRs touching `Executor/**`, and manual dispatch | `Executor/tests/integration_test.sh`, `Executor.OpenCode/tests/integration_test.sh`, `tests/e2e/` |
| Public API surface | Webhook ingress `OnWebhook` handler in `TheAgent/Agent/XianixAgent.cs:176`; rule models `TheAgent/Rules/WebhookRulesModels.cs`; `TheAgent/Knowledge/rules.json`; agent tools `TheAgent/Agent/*SubagentTools.cs`; `Docs/rules-json.md`, `Docs/webhook-headers-guide.md`. No route/controller directories | Orchestrator `TheAgent/Orchestrator/` (4 files) | `TheAgent.Tests/Orchestrator/EventOrchestratorTests.cs`, `Rules/WebhookRulesEvaluatorTests.cs` |
| DB migrations & schema | Not found in repository | — | — |
| Security configuration | Webhook verification (above); deduplication `TheAgent/Orchestrator/WebhookDeduplicationGuard.cs`. CORS, security headers, rate limiting: Not found in repository | — | WebhookVerificationGateTests.cs |
| Concurrency-sensitive | `TheAgent/Workflows/` (`JobDispatcherWorkflow`, `ProcessingWorkflow`, `CognitiveDispatcher`, `OnboardRepositoryWorkflow`, `ClaudeCodeChatWorkflow` with `[WorkflowRun]`); `TheAgent/Rules/Schedule/` (cron); `TheAgent/Orchestrator/WebhookDeduplicationGuard.cs`; `Executor/maintain_volume.sh` | Workflow and schedule handlers; `ClaudeCodeChatWorkflow`/`OnboardRepositoryWorkflow` `[Workflow]` attributes are commented out in source | `TheAgent.Tests/Workflows/` (2), `Orchestrator/` |
| Recently modernised dirs | Not found in repository (Docs/ai-dlc-ping-pong.md §10 "Migration from the existing rules" is a design doc; Docs/tenant-isolation-design.md:311 lists `TenantWorkspaceActivities.cs` as "Deprecated → remove", file not tracked) | — | — |

**Executor / Executor.OpenCode**
| Sub-category | Path | Reason / facts |
|---|---|---|
| Auth | `Executor/prepare_repo.sh`, `Executor.OpenCode/prepare_repo.sh` | Repository preparation script (by file name); gh and az CLIs installed (Executor/Dockerfile) |
| Infrastructure | Dockerfiles, `entrypoint.sh`, `provision_runtimes.sh` | Installs runtimes at container runtime; `semgrep`, `bandit`, `pip-audit` installed (Executor/Dockerfile) |
| Test coverage | `Executor/tests/`, `Executor.OpenCode/tests/` | unittest + integration_test.sh |

**Other sub-categories** (payments, DB migrations): Not found in repository.

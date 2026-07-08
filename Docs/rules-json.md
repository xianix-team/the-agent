# Rules Configuration (`rules.json`)

The rules file is the single configuration surface that controls **what the agent does** when a webhook arrives. Each entry in the JSON array is a self-contained rule set that maps a webhook name to one or more **execution blocks** — each block defines payload filters, input extraction, plugin installation, and a templated prompt for a Claude Code session in the executor container.

```
rules.json  →  WebhookRulesEvaluator  →  EventOrchestrator  →  ProcessingWorkflow  →  Executor Container
```

In **this repository**, the default rules are embedded from [`TheAgent/Knowledge/rules.json`](../TheAgent/Knowledge/rules.json) and uploaded as Xians knowledge document **`Rules`** (`Constants.RulesKnowledgeName`).

---

## File structure

`rules.json` is a JSON array of **rule set** objects. Each rule set targets one **webhook** name (case-insensitive) and contains an **executions** array. Each execution is an independent pipeline: optional filters, inputs, plugins, and prompt.

```jsonc
[
  {
    "webhook": "...",
    "with-envs": [ ... ],
    "executions": [
      {
        "name": "...",
        "platform": "...",
        "repository": {
          "url":  "...",
          "name": "...",
          "ref":  "..."
        },
        "match-any": [ ... ],
        "use-inputs": [ ... ],
        "use-plugins": [ ... ],
        "with-envs":   [ ... ],
        "execute-prompt": "..."
      }
    ]
  }
]
```

| Field | Description |
|-------|-------------|
| `webhook` | Webhook name from Xians Agent Studio (must match incoming events) |
| `with-envs` (optional, on the rule set) | Rule-set-wide [common environment variables](#5-with-envs--container-environment-variables) injected into every execution in this rule set. Per-execution `with-envs` entries override these by env name. |
| `executions` | One or more execution blocks |
| `platform` (optional, on each execution) | Hosting service the execution operates against (`github`, `azuredevops`, …). Structural — describes *where* the run happens, independent of the plugin. Auto-injected into `XIANIX_INPUTS` as `"platform"` for plugin prompts. Omit for executions that don't target a specific platform. |
| `repository` (optional, on each execution) | Structural binding for the repository being operated on. Every sub-field that is declared (`url`, `ref`) is treated as **mandatory** — if any declared path doesn't resolve, the block is skipped before any container starts. Auto-injected as `"repository-url"` / `"git-ref"`, with `"repository-name"` derived from `repository.url` and injected alongside them. Omit entirely for executions that don't operate on a specific repo (e.g. work-item analysis). |
| `compression` (optional, on each execution) | Boolean opt-in for the per-run [Headroom](https://github.com/headroomlabs-ai/headroom) context-compression proxy (Option B — see [`Docs/headroom-compression-design.md`](./headroom-compression-design.md)). When `true`, the executor starts a local Headroom proxy for **only this run** and routes Claude Code through it via `ANTHROPIC_BASE_URL`; compression stats are emitted through the executor's stdout envelope and reported to the central metrics dashboard under the `compression` category. Defaults to `false`. Surfaced to the container as `XIANIX-COMPRESSION=1`. Fail-open — a proxy start-up failure never fails the run. |

If **several** execution blocks in the same rule set match the same webhook payload, **each** match is scheduled separately: the integrator starts one activation / processing workflow per match (see `XianixAgent` webhook handler).

### Evaluation flow

```
┌──────────────────────────────────────────────────────────────────────┐
│  Incoming Webhook                                                    │
│  name: "Default"   payload: { "action": "opened", ... }              │
└───────────────────────────────┬──────────────────────────────────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Find rule set where  │
                    │  webhook matches      │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  For each execution:  │
                    │  Evaluate match-any   │──── No match? → skip block
                    │  (OR across entries)  │
                    └───────────┬───────────┘
                                │ At least one match-any passes
                    ┌───────────▼───────────┐
                    │  Extract use-inputs   │
                    │  from payload         │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Interpolate          │
                    │  execute-prompt       │
                    │  with {{input-name}}  │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Start executor with  │
                    │  plugins + prompt     │
                    └───────────────────────┘
```

---

## 1. `webhook`

Case-insensitive match against the webhook name configured in Xians Agent Studio.

```json
"webhook": "Default"
```

Only one rule set per webhook name is used — the **first** matching entry in the `rules.json` array wins.

---

## 1b. `platform` & `repository` — Structural execution context

These two execution-level fields describe **what the run operates on** — independent of which plugin is used. They're resolved before any plugin runs, used by the framework itself (credential setup, workspace volume, worktree checkout, chat-side input resolution), **and** auto-injected into `XIANIX_INPUTS` under canonical kebab-case keys so plugin prompts and the executor entrypoint can read them off the same keys they always have.

```json
"platform": "github",
"repository": {
  "url": "repository.clone_url",
  "ref": "pull_request.head.ref"
}
```

| Field             | Type                                                                | Description |
|-------------------|---------------------------------------------------------------------|-------------|
| `platform`        | string literal                                                      | Hosting service (`github`, `azuredevops`, …). Used by the executor to pick the right `git` credential helper and is exposed to plugin prompts as `{{platform}}`. Empty / omitted means the executor will infer from the repo URL (defaults to `github`). |
| `repository.url`  | string (JSON path) **or** `{ value, constant }` object              | Either a JSON path that resolves to the clone URL (the common webhook-driven case) or a hard-coded literal via the constant form (see below). **Mandatory when declared** — if a declared JSON path doesn't resolve, the execution block is skipped before any container starts. Exposed as `{{repository-url}}`. |
| `repository.ref`  | string (JSON path) **or** `{ value, constant }` object              | Either a JSON path that resolves to the git ref (branch, commit SHA, or tag), or a constant pinning the run to a fixed branch/tag. **Mandatory when declared.** Omit entirely to run against the bare-clone HEAD. Exposed as `{{git-ref}}` and used directly by `Executor/entrypoint.sh` to position the worktree before the prompt runs. |

> **`{{repository-name}}` is derived, not declared.** A short `owner/repo`-style identifier is computed from the resolved `repository.url` (platform-aware: GitHub, Azure DevOps `_git` URLs, etc.) and auto-injected as `{{repository-name}}`. There is no `repository.name` knob in the schema — clone URL and display name are kept in lockstep so they can never drift.
>
> If you need a different display name, pick a different clone URL — that's the single source of truth.

#### Hard-coding the repository (constant form)

For runs whose repository or ref is fixed regardless of the webhook payload — cron pings, Slack triggers, single-tenant agents pinned to one repo, manual triggers — wrap the value in `{ "value": "...", "constant": true }`:

```jsonc
"repository": {
  "url": { "value": "https://github.com/my-org/agent-target.git", "constant": true },
  "ref": { "value": "main",                                          "constant": true }
}
```

The bare-string shorthand (`"url": "repository.clone_url"`) is just sugar for `{ "value": "repository.clone_url", "constant": false }`, so existing rules need no changes. Mixed forms work too — clone a fixed mirror but check out whatever ref the webhook says:

```jsonc
"repository": {
  "url": { "value": "https://github.com/my-org/mirror.git", "constant": true },
  "ref": "pull_request.head.ref"
}
```

Constant URLs of course also drive `{{repository-name}}` — `RepositoryNaming.DeriveName` runs on the resolved URL regardless of how it was supplied.

### Why split these out from `use-inputs`?

- They are **structural** — every webhook-triggered run on a repo needs them, regardless of plugin. Promoting them to execution-level removes per-plugin duplication and makes the contract explicit.
- The framework needs them **before** the plugin loop runs (clone target, credential helper, volume name, worktree ref) — they were already special-cased; now the schema reflects that.
- `repository.ref` is part of the *binding* (which repo, at which ref), not a free-form input the prompt happens to use — nesting it next to `url` keeps that relationship obvious.
- The chat-driven path (`SupervisorSubagentTools.RunClaudeCodeOnRepository`) treats `RepositoryUrl` / `RepositoryName` as first-class typed fields and derives the name from the URL the same way the webhook path does, via `RepositoryNaming.DeriveName`. Aligning the webhook schema removes a subtle divergence.
- Executions that don't operate on a repo (e.g. Azure DevOps work-item analysis) just **omit** the `repository` block — no need for `mandatory: false` ceremony on per-plugin inputs.

### Wire-format

Plugin prompts and `Executor/entrypoint.sh` always read structural values from these canonical `XIANIX_INPUTS` keys (`platform`, `repository-url`, `repository-name`, `git-ref`). The agent serialises the resolved structural values into the inputs dict under exactly these keys — they are **not** authored under `use-inputs` and the same key names are not used for anything else. `repository-name` is the derived value (from `repository.url`), not a separate path.

---

## 2. `match-any` — Payload filtering

Inside each execution block, `match-any` is an array of filter rules evaluated with **OR logic**: the block passes if **any** entry matches. If `match-any` is omitted or empty, the block passes unconditionally.

```json
"match-any": [
  { "name": "pr-opened-event",       "rule": "action==opened" },
  { "name": "pr-synchronize-event",  "rule": "action==synchronize" }
]
```

| Field  | Description |
|--------|-------------|
| `name` | Human-readable label (for logging and skip reasons) |
| `rule` | A filter expression — see syntax below |

### Filter expression syntax

Each rule is a comparison of a **JSON path** against a **literal value**, optionally combined with `&&` (AND) and `||` (OR) operators:

```
<json-path> <operator> <expected-value>
```

| Operator | Meaning       | Missing path returns |
|----------|---------------|----------------------|
| `==`     | Equals        | `false`              |
| `!=`     | Not equals    | `true`               |

### Compound expressions

Multiple conditions can be combined in a single rule using `&&` (AND) and `||` (OR):

| Operator | Meaning | Precedence |
|----------|---------|------------|
| `&&`     | AND — all conditions in the group must be true | Higher |
| `||`     | OR — at least one group must be true           | Lower  |

`||` has lower precedence than `&&`. The rule is split into OR-groups first, then each group is split into AND-conditions.

```jsonc
"rule": "eventType==workitem.updated&&status==Active"
"rule": "action==opened||action==reopened"
"rule": "eventType==created&&status==New||eventType==updated&&status==Active"
```

### Quoted values

If the expected value contains `&&` or `||` (or you want a single-quoted literal), wrap it in **single quotes**:

```jsonc
"rule": "assignee=='some-user <user@example.com>'"
```

### JSON paths

JSON paths use dot notation to traverse the payload.

| Expression                   | Notes |
|-----------------------------|--------|
| `pull_request.draft==false` | Nested objects |

Type coercion is handled automatically — strings, numbers, booleans, and `null` are compared against the literal on the right-hand side.

#### Property names that contain `.`

If an object **key** contains a dot (common on Azure DevOps, e.g. `System.AssignedTo`), wrap **that segment** in **double quotes** so it is treated as a single property name:

```
resource.fields."System.AssignedTo".newValue
resource.revision.fields."System.Title"
```

Inside a double-quoted segment, a **backslash** escapes the next character. This applies to **match** rules and to **`use-inputs`** paths.

#### Arrays: numeric indices

When the value at a path segment is a JSON **array**, a **numeric** segment selects the element at that index (zero-based):

```
items.0.id
resource.reviewers.1.displayName
```

If the index is out of range, the path does not resolve (`==` fails; `!=` treats a missing path as not equal).

#### Arrays: wildcard `*` (match rules only)

For **filter rules** (`match-any`), a path segment `*` means “any element of the array at this point.” The prefix before `*` must resolve to an array.

```
resource.reviewers.*.displayName=='xianix-agent'
```

Only **one** `*` segment per path is supported. Wildcard `*` is **not** supported in **`use-inputs`** paths — use a fixed numeric index if you need a specific array element.

**Implementation:** `TheAgent/Rules/WebhookRulesEvaluator.cs` (`SplitJsonPathSegments`, `TryGetElementAtPath`, wildcard handling in `EvaluatePathCompare`).

---

## 3. `use-inputs` — Payload extraction

Extracts values from the webhook payload into named variables. They are used for `execute-prompt` interpolation and are forwarded to the executor (for example as `XIANIX_INPUTS`).

> **Don't put structural context here.** `platform`, `repository-url`, `repository-name`, and `git-ref` are declared at the [execution level](#1b-platform--repository--structural-execution-context) and auto-injected into `XIANIX_INPUTS` for you. Authoring them under `use-inputs` is unsupported — the framework uses the structural fields for credential setup, volume management, worktree checkout, and chat-side input validation.

```json
"use-inputs": [
  { "name": "pr-number", "value": "number",             "mandatory": true },
  { "name": "pr-title",  "value": "pull_request.title" }
]
```

| Field       | Description |
|-------------|-------------|
| `name`      | Key in the extracted dictionary |
| `value`     | Dot-separated JSON path into the payload, **or** a literal when `constant` is `true` |
| `constant`  | *(optional, default `false`)* When `true`, `value` is used as-is instead of resolving a path |
| `mandatory` | *(optional, default `false`)* When `true`, the execution block is **skipped before any container is started** if this input resolves to `null`, an empty string, or whitespace. Use this to fail fast when the webhook payload is missing data the prompt depends on. |

### Path resolution examples

Given:

```json
{
  "number": 42,
  "repository": { "clone_url": "https://github.com/acme/app.git", "full_name": "acme/app" },
  "pull_request": { "title": "Fix auth bug", "head": { "ref": "fix/auth" } }
}
```

| Input `value` | Resolved value |
|---------------|----------------|
| `number` | `42` |
| `repository.clone_url` | `https://github.com/acme/app.git` |
| `pull_request.head.ref` | `fix/auth` |
| `github` with `"constant": true` | `github` (literal) |

For Azure DevOps payloads, dotted field names use the same quoted-segment syntax as in filters, e.g. `resource.revision.fields."System.Title"`.

If a path does not resolve (missing property), the input is set to `null`. If the input is also marked `"mandatory": true`, the execution block is skipped (with an explicit error logged) and **no executor container is started** for that block — other matching blocks are unaffected.

---

## 4. `use-plugins` — Plugin installation

Declares Claude Code marketplace plugins to install in the executor container before the prompt runs.

```json
"use-plugins": [
  {
    "plugin-name": "pr-reviewer@xianix-plugins-official",
    "marketplace": "xianix-team/plugins-official"
  }
]
```

| Field           | Required | Description |
|-----------------|----------|-------------|
| `plugin-name`   | Yes | Plugin reference in `plugin-name@marketplace-name` form, passed to `claude plugin install` |
| `marketplace`   | No  | Marketplace source (`owner/repo`, git URL, path, or `marketplace.json` URL). Omit for the built-in Anthropic marketplace. |

> **Note** — credentials the plugins need are no longer declared per-plugin. They live at the execution level in [`with-envs`](#5-with-envs--container-environment-variables) so a value like `GITHUB-TOKEN` only has to be written once even when several plugins consume it.

---

## 5. `with-envs` — Container environment variables

Declares environment variables to inject into the executor container before the prompt runs. `with-envs` can be authored at **two levels**:

1. **Rule-set level** (sibling to `webhook` / `executions`) — *common* envs that apply to **every** execution in the rule set. Use these to declare credentials or settings every execution shares so the same line doesn't have to be repeated on each block. Per-execution `with-envs` entries can override these by env name (same name → execution-level wins).
2. **Execution-block level** (sibling to `use-plugins`) — envs specific to one execution. Layered on top of the rule-set-level common envs.

```jsonc
[
  {
    "webhook": "Default",
    "with-envs": [
      // Common to every execution in this rule set — no need to repeat per execution.
      { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
    ],
    "executions": [
      {
        "name": "azuredevops-pull-request-review",
        "platform": "azuredevops",
        "with-envs": [
          // Adds an Azure DevOps PAT only to this execution. The rule-set-level
          // GITHUB-TOKEN is still injected here too.
          { "name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": true }
        ],
        // …
      },
      {
        "name": "feature-flag-experiment",
        "with-envs": [
          // Same NAME as the rule-set entry → this execution-level override wins.
          { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN-LEGACY", "mandatory": true },
          { "name": "FEATURE-FLAG-MODE", "value": "strict", "constant": true }
        ],
        // …
      }
    ]
  }
]
```

| Field       | Description |
|-------------|-------------|
| `name`      | Env var name inside the container |
| `value`     | Must use one of three explicit forms: `host.VAR_NAME` (read from the **agent host** environment), `secrets.SECRET-KEY` (fetched from the **tenant Secret Vault** via `XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)` at container-start time), or a literal string when `"constant": true`. **Bare names and unknown prefixes (including the legacy `env.X`) fail the activation with a non-retryable error** — for credentials, "I don't know where to read this from" must never silently become "I quietly read it from the host". |
| `constant`  | *(optional)* Treat `value` as a literal |
| `mandatory` | *(optional, default `false`)* When `true`, the executor container **fails to start** (non-retryable) if this env resolves to `null` or empty. Use for credentials the prompt cannot run without. |

### Override semantics (rule-set vs execution)

The two levels are merged **before** the container starts:

- Every rule-set-level entry is included unless an execution declares an entry with the same `name`.
- Execution-level entries always win on a name collision — both `value` and `mandatory` are taken from the execution-level entry. The rule-set-level entry is dropped for that execution (so a rule-set `mandatory: true` can't trip the missing-mandatory check after the execution has explicitly overridden it).
- The emitted order is "common defaults first, per-execution last" — operator-friendly when scanning the env-provenance log.

Examples:

| Rule-set declares                                              | Execution declares                                                    | Effective env list for this run                                              |
|----------------------------------------------------------------|------------------------------------------------------------------------|------------------------------------------------------------------------------|
| `GITHUB-TOKEN` (secrets.X, mandatory)                          | *(no `with-envs`)*                                                     | `GITHUB-TOKEN=secrets.X` (mandatory)                                         |
| `GITHUB-TOKEN` (secrets.X, mandatory)                          | `AZURE-DEVOPS-TOKEN` (secrets.Y, mandatory)                            | `GITHUB-TOKEN=secrets.X` (mandatory), `AZURE-DEVOPS-TOKEN=secrets.Y` (mand.) |
| `GITHUB-TOKEN` (secrets.X, mandatory)                          | `GITHUB-TOKEN` (secrets.Z, optional) — same name, override             | `GITHUB-TOKEN=secrets.Z` (optional) — execution wins                         |

### Chat-driven runs

The same `with-envs` declarations also flow through to **chat-initiated** runs (e.g. when a user asks the agent to run a plugin via `RunClaudeCodeOnRepository` instead of via a webhook). A chat dispatch doesn't bind to a specific execution block, so the chat tool reads `rules.json` as the manifest of *every* credential the agent could need and ships:

- **Every rule-set-level `with-envs` entry** — applied unconditionally, regardless of platform. This is precisely the "common defaults" contract: a `GITHUB-TOKEN` declared at the rule-set level is available to chat runs the same way it's available to every execution.
- **Per-execution `with-envs` entries** whose execution matches the chosen repository's platform (or is platform-agnostic) — kept under the platform filter so a GitHub-targeted chat run doesn't inherit Azure DevOps's mandatory PAT and vice versa.

Both lists are then deduped by env name. The platform filter intentionally does *not* apply to rule-set commons — if you want a credential to be platform-specific, declare it under the matching execution(s), not at the rule-set level.

### Resolution precedence

When the host `.env` (or Key Vault on the deployed VM) declares `ANTHROPIC-API-KEY`, that value is seeded into the executor container as a default. If the host does **not** declare it, no seed is emitted and the container is expected to receive the key from a `with-envs` entry in `rules.json` instead. All CM platform credentials — `GITHUB-TOKEN`, `AZURE-DEVOPS-TOKEN`, anything else — are **not** read from the host: each tenant must store their own in the Xians Secret Vault and reference it from `rules.json` via `"value": "secrets.<KEY>"`. `with-envs` entries are layered on top of the host-derived defaults at container-start time, so any `secrets.*` or `host.*` entry in `rules.json` overrides whatever was seeded.

### Agent-process credentials (e.g. `ANTHROPIC-API-KEY`)

Some credentials are consumed by the agent process itself, not just the container — `ANTHROPIC-API-KEY` is the headline example (the supervisor chat subagent calls Claude directly). For those, the rule-set-level `with-envs` is honoured on the supervisor's **first chat message per tenant** with a "rules-first, host-fallback" policy implemented by `StartupEnvResolver`:

1. On the first incoming chat message for a given tenant, the supervisor reads the uploaded `rules.json` knowledge document via the canonical `RulesKnowledge.LoadAsync` reader (same call site every other rules consumer uses) and walks every rule set's top-level `with-envs`. Match is by exact `name`, first-wins across rule sets.
2. If a matching entry is found, it is resolved against the **current tenant's** scope (`XiansContext.CurrentAgent` is bound to the calling message's tenant via AsyncLocal at that point):
   - `"constant": true` → value taken verbatim.
   - `"value": "host.VAR_NAME"` → looked up on the host process env.
   - `"value": "secrets.SECRET-KEY"` → fetched from the **tenant-scoped Xians Secret Vault** (`XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)`). Each tenant therefore plugs in their own API key; the supervisor caches one `AIAgent` per tenant so each gets their own `AnthropicClient`. A missing vault entry resolves to `null` and the resolver falls back to the host env — same semantics as the container path.
3. If resolution fails or no entry is declared, the supervisor falls back to the host env var of the same name (`EnvConfig.AnthropicApiKey`).

The host env var is **optional**. `EnvConfig.ValidateRequiredVariables()` gates only the Xians platform credentials (`XIANS-SERVER-URL`, `XIANS-API-KEY`) so the agent can register and upload knowledge before any chat traffic arrives — Anthropic key absence does not block boot. If neither the rule-set-level entry nor the host env resolves at first-message time, the supervisor raises a clear `Anthropic API key resolver returned an empty value for tenant '<tenant>'` error which the conversation workflow logs and surfaces to the user as a generic apology; fix it by adding either a rule-set-level `with-envs` entry (constant / `host.*` / `secrets.*`) or a host env value. No restart needed — `SupervisorSubagent.EnsureAgentForTenantAsync` only caches successful constructions, so the next message from that tenant retries the resolver. When a host value is present, a rule-set-level `ANTHROPIC-API-KEY` entry in `rules.json` *overrides* it on the first chat message per tenant, after which the resolved `AnthropicClient` is cached for the lifetime of the process *for that tenant*. The container path also picks up rule-set commons via the normal `with-envs` merge, so the same declaration covers both the supervisor and every executor run.

### Resolving `secrets.*`

```json
{ "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
```

At container-start time the agent resolves `secrets.GITHUB-TOKEN` by calling:

```csharp
var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
var fetched = await vault.FetchByKeyAsync("GITHUB-TOKEN");
```

The decrypted value is injected as the named env var into the executor container, overriding any host-loaded value with the same name. If the secret is missing, the value resolves to an empty string — combine with `"mandatory": true` to fail-fast when the secret is required.

---

## 6. `execute-prompt` — Claude Code prompt template

A string template run as the Claude Code prompt after plugins are installed. Use `{{input-name}}` placeholders for resolved `use-inputs` values.

Placeholders are replaced case-insensitively. Any `{{name}}` with no matching input is left unchanged.

---

## Complete example (GitHub PR opened)

```json
[
  {
    "webhook": "Default",
    "with-envs": [
      { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
    ],
    "executions": [
      {
        "name": "github-pull-request-review",
        "platform": "github",
        "repository": {
          "url": "repository.clone_url",
          "ref": "pull_request.head.ref"
        },
        "match-any": [
          { "name": "pr-opened-event", "rule": "action==opened" }
        ],
        "use-inputs": [
          { "name": "pr-number", "value": "number" },
          { "name": "pr-title",  "value": "pull_request.title" }
        ],
        "use-plugins": [
          {
            "plugin-name": "pr-reviewer@xianix-plugins-official",
            "marketplace": "xianix-team/plugins-official"
          }
        ],
        "execute-prompt": "You are reviewing pull request #{{pr-number}} titled \"{{pr-title}}\" in the repository {{repository-name}} (branch: {{git-ref}}).\n\nRun /code-review to perform the automated review. The `gh` CLI is authenticated and available if you need it directly."
      }
    ]
  }
]
```

`GITHUB-TOKEN` is declared once at the rule-set level so every execution under `Default` picks it up — there's no need to repeat the env entry on each execution. An Azure DevOps execution sharing the same rule set would simply add its `AZURE-DEVOPS-TOKEN` under its own `with-envs` block.

### Work-item example (no repository)

When the run doesn't operate on a specific repo, just omit the `repository` block — the executor is happy to spin up an empty workspace:

```jsonc
{
  "name": "azuredevops-work-item-requirement-analysis",
  "platform": "azuredevops",
  "match-any": [
    { "rule": "eventType==workitem.updated&&resource.fields.\"System.AssignedTo\".newValue=='xianix-agent <xianix-agent@99x.io>'" }
  ],
  "use-inputs": [
    { "name": "workitem-id", "value": "resource.workItemId" }
  ],
  "use-plugins": [ /* … */ ],
  "execute-prompt": "Run /requirement-analysis {{workitem-id}}."
}
```

### Azure DevOps: work item field with a dotted name

```jsonc
"rule": "eventType==workitem.updated&&resource.fields.\"System.AssignedTo\".newValue=='xianix-agent <xianix-agent@99x.io>'"
```

### Azure DevOps: PR updated with a specific reviewer

```jsonc
"rule": "eventType==git.pullrequest.updated&&resource.reviewers.*.displayName=='xianix-agent'"
```

### What happens at runtime

1. Webhook payload arrives; orchestrator evaluates rules for the webhook name.
2. For each execution block, if `match-any` is non-empty, at least one `rule` must pass.
3. The structural fields (`platform`, `repository.url`, `repository.ref`) are resolved alongside `use-inputs`. Any declared structural field that fails to resolve **skips the block** with a clear error — same code path as a missing mandatory input.
4. The resolved structural values are auto-injected into the inputs dictionary as `platform`, `repository-url`, and `git-ref`. The short `repository-name` (e.g. `owner/repo`) is **derived** from `repository-url` via `RepositoryNaming.DeriveName` (platform-aware: handles GitHub, Azure DevOps `_git` URLs, etc.) and injected alongside them — these are the canonical wire-format keys plugin prompts and the executor entrypoint expect.
5. `execute-prompt` is interpolated against the merged inputs dict.
6. The agent merges rule-set-level common `with-envs` with the matched execution's own `with-envs` (execution-level entries override rule-set entries by env name), resolves each entry (literals, `host.*`, `secrets.*`), and injects them into the executor container alongside the runtime values it manages itself.
7. The executor uses `platform` to pick the right credential helper, `git clone`s `repository-url` into the per-tenant workspace volume, checks out `git-ref` into the per-run worktree (or HEAD when omitted), installs `use-plugins`, and runs the prompt.

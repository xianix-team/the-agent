# Rules Configuration (`rules.json`)

The rules file is the single configuration surface that controls **what the agent does** when a webhook arrives. Each entry in the JSON array is a self-contained rule set that maps a webhook name to one or more **execution blocks** — each block defines payload filters, input extraction, plugin installation, and a templated prompt for a Claude Code session in the executor container.

```
rules.json  →  WebhookRulesEvaluator  →  EventOrchestrator  →  ProcessingWorkflow  →  Executor Container
```

In **this repository**, the default rules are embedded from [`TheAgent/Knowledge/rules.json`](../TheAgent/Knowledge/rules.json) and uploaded as Xians knowledge document **`Rules`** (`Constants.RulesKnowledgeName`) at **system scope** on agent startup (Studio: System).

**Scope rules for Rules Optimizer:**

| When | Where Rules live | Studio label |
|------|------------------|--------------|
| Initial seed (prompts + empty `rules.json`) | `systemScoped=true` | System |
| After `InstallPlugins` / `SaveRules` | `systemScoped=false` + `activationName` | Agent |

Do not write plugin installs back to system or organization scope.

---

## Three sources of truth (Rules Optimizer)

Rules Optimizer uses the Xians.Lib SDK for tenant-scoped secrets, webhooks, and Rules knowledge; an Admin API key is not required on the agent host.

Rules Optimizer no longer treats `rules.json` as both the plugin catalog and the installed configuration. Keep these separate:

| Concern | Source | Notes |
| --------- | -------- | ------- |
| **Available plugins** | Official marketplace only ([`marketplace.json`](https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json)), fetched live — no alternate catalog fallback | Full listing shown to the user. Test copies live under `TheAgent.Tests/Fixtures/marketplace.json` only. |
| **Ready to install** | Marketplace entry **and** a live plugin [`README.md`](https://github.com/xianix-team/plugins-official/blob/main/plugins/pr-reviewer/README.md) at `plugins/<folder>/README.md` (folder from marketplace `source`) **and** a local execution recipe | Secrets, triggers, webhook events, and execution templates come from `TheAgent.Tests/Fixtures/agent-setup/<name>/agent-setup.json` (copied to the agent as `PluginRecipes/`). Do **not** fetch remote `.xianix/agent-setup.json`. |
| **Coming soon** | Marketplace entry without a fetchable README, or without a local execution recipe | Listed but not installable |
| **Installed plugins** | Agent-scoped `rules.json` `use-plugins` entries (Studio: Agent = activation override; webhook root + executions + chat rule sets) | Deduplicated union; system seed stays empty until first save |

A **fresh activation** starts from this skeleton (no installed plugins):

```json
[
  {
    "webhook": "Default",
    "with-envs": [],
    "use-plugins": [],
    "executions": []
  },
  {
    "chat": "chat",
    "use-plugins": [],
    "model": "claude-sonnet-4-5",
    "max-budget-usd": 5.0
  }
]
```

Rules Optimizer loads phase-specific **skills** (under `Knowledge/skills/rules-optimizer/`) via `LoadRulesOptimizerSkill`; low-level C# tools remain the implementation layer.

---

## File structure

`rules.json` is a JSON array of **rule set** objects. The *kind* of each rule set is chosen by its discriminator key at the **root level** of the object — mutually exclusive:

| Root key | Kind | Consumed by | Section |
| ---------- | ------ | ------------- | --------- |
| `"webhook"` | Webhook rule set — reacts to an inbound event | `WebhookRulesEvaluator` | below |
| `"chat"` | Chat rule set — the plugin invocations the chat tool may offer | `AvailablePluginsCatalog` | [1a](#1a-chat--root-level-chat-rule-sets) |
| `"schedule"` (+ `"cron"`) | Schedule rule set — cron-driven runs | `ScheduleEvaluator` | (see schedule docs) |

Every kind carries an **executions** array (each execution is an independent pipeline: optional filters, inputs, plugins, prompt) and may carry a rule-set-wide `with-envs`. Objects without one of these discriminator keys are ignored by every reader. A webhook rule set targets one **webhook** name (case-insensitive).

```jsonc
[
  {
    "webhook": "...",
    "with-envs": [ ... ],
    "executions": [
      {
        "name": "...",
        "platform": "...",
        "repository": "...",
        "match-any": [ ... ],
        "use-inputs": [ ... ],
        "use-plugins": [ ... ],
        "with-envs":   [ ... ],
        "execute-prompt": "...",
        "raise-events": [ ... ]
      }
    ]
  }
]
```

| Field | Description |
| ------- | ------------- |
| `webhook` | Webhook name from Xians Agent Studio (must match incoming events) |
| `with-envs` (optional, on the rule set) | Rule-set-wide [common environment variables](#5-with-envs--container-environment-variables) injected into every execution in this rule set. Per-execution `with-envs` entries override these by env name. |
| `executions` | One or more execution blocks |
| `platform` (optional, on each execution) | Hosting service the execution operates against (`github`, `azuredevops`, …). Structural — describes *where* the run happens, independent of the plugin. Auto-injected into `XIANIX_INPUTS` as `"platform"` for plugin prompts. Omit for executions that don't target a specific platform. |
| `repository` (optional, on each execution) | Structural binding for the repository being operated on. Declared sub-fields (`url`) are treated as **mandatory** — if a declared path doesn't resolve, the block is skipped before any container starts. Auto-injected as `"repository-url"`, with `"repository-name"` derived from `repository.url` and injected alongside it. The executor always checks out the default-branch HEAD; plugins perform any task-specific checkout. Omit entirely for executions that don't operate on a specific repo (e.g. work-item analysis), or for chat rule-set executions (the chat tool always supplies the repository itself). |

If **several** execution blocks in the same rule set match the same webhook payload, **each** match is scheduled separately: the integrator starts one activation / processing workflow per match (see `XianixAgent` webhook handler).

### Evaluation flow

```
┌──────────────────────────────────────────────────────────────────────┐
│  Incoming Webhook                                                    │
│  name: "Default"   payload: { "action": "opened", ... }              │
└───────────────────────────────┬──────────────────────────────────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Find webhook rule    │──── (chat / schedule rule sets have no
                    │  set where name       │      webhook name → never selected here)
                    │  matches              │
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

## 1a. `chat` — root-level chat rule sets

A **chat rule set** is a root-level sibling of the `"webhook"` and `"schedule"` rule sets — the invocation path is chosen by *which discriminator key the object carries*, not by a per-execution flag. A chat rule set is keyed on `"chat"` and lists the marketplace plugins the chat tool (`SupervisorSubagentTools.RunClaudeCodeOnRepository`) may offer the user, via `AvailablePluginsCatalog`, along with the cost/control tuning to apply to those chat runs.

Unlike a webhook rule set, **a chat rule set has no `executions` array.** A chat run's prompt is authored by the supervisor from the user's own message — there's no payload to interpolate, so a per-execution `execute-prompt` / `use-inputs` shape would be redundant. The plugin list and tuning knobs live at the rule-set **root** and apply uniformly to every chat dispatch that uses one of the listed plugins:

```jsonc
{
  "chat": "chat",
  "with-envs": [ /* optional, rule-set-wide — same shape as a webhook rule set */ ],
  "use-plugins": [
    {
      "plugin-name": "pr-reviewer@xianix-plugins-official",
      "marketplace": "xianix-team/plugins-official",
      "slash-command": "/pr-review"
    }
  ],
  "model": "claude-sonnet-4-5",
  "max-budget-usd": 5.0
  // also: "max-turns", "allowed-tools", "disallowed-tools", "resume-sessions"
}
```

| Root key | Consumed by | Notes |
| ---------- | ------------- | ------- |
| `"webhook"` | `WebhookRulesEvaluator` against inbound webhook payloads | Matches on the webhook name; has `executions`. |
| `"chat"` | `AvailablePluginsCatalog` (feeds the chat tool's `ListAvailablePlugins`) | Has no webhook name, so the webhook evaluator **never** selects it. No `executions` — `use-plugins` + tuning at the root. |
| `"schedule"` (+ `"cron"`) | `ScheduleEvaluator` | Cron-driven; parsed independently from the same document. |

`RulesKnowledge.LoadAsync` keeps only rule sets with a non-empty `"webhook"` name and `RulesKnowledge.LoadChatRuleSetsAsync` keeps only those with a non-empty `"chat"` name, so the three kinds coexist in one array without leaking into each other's readers.

### Root-level fields

| Field | Description |
| ------- | ------------- |
| `chat` | Discriminator + label. Any non-empty value marks the object as a chat rule set (the value is only used in logs — there's no external event to match). |
| `use-plugins` | The plugins this rule set makes available to the chat tool. Same `plugin-name@marketplace` shape as a webhook execution's `use-plugins`, plus a required **`slash-command`** (e.g. `/pr-review`) so the supervisor composes `{slash-command} {user-target}` without inventing a command name. |
| `model` / `max-turns` / `allowed-tools` / `disallowed-tools` / `max-budget-usd` / `resume-sessions` | Cost/control tuning applied to every chat dispatch that uses one of `use-plugins`. Same meaning as the identically-named per-execution knobs on a webhook block. |
| `with-envs` | Optional rule-set-wide common environment variables shipped to chat dispatches by `RulesEnvCatalog` (platform-agnostic, like a webhook rule set's commons). |

### Why cost tuning must live on the chat rule set

If the chat catalog had to reuse a plugin's *webhook* execution blocks for its tuning, a chat run would silently inherit (or lose) whatever cost tuning the webhook block happened to carry. A webhook PR review might be pinned to `claude-sonnet-4-5` at `max-budget-usd: 3.0`, but without a chat rule set the chat tool has no equivalent knobs to populate on its dispatch — chat runs fall back to the executor's untuned defaults (a pricier model, a higher budget cap). Declaring `model` / `max-budget-usd` / … on the chat rule set fixes that, and lets the chat budget differ from the webhook budget for the same plugin.

Author **separate chat rule sets** when different plugins need different tuning.

### Invocation guidance — slash command, not invented prompts

Because a chat run's prompt comes from the user's message, a chat rule set carries no `execute-prompt`. The supervisor composes the prompt as `{slash-command} {target}` using the **`slash-command` declared on the chat `use-plugins` entry** (surfaced by `ListAvailablePlugins` as `slashCommand`), appending whatever target the user gave (a PR number, a branch name, …) — e.g. "review PR 42" → `/pr-review 42`, "review my `feature/login` branch" → `/pr-review feature/login`. It must **not** invent alternate command names from the plugin name (e.g. do not turn `pr-reviewer` into `/code-review`). This is why one plugin listing serves both the "by PR number" and "by branch" cases that previously needed two execution blocks.

`ListAvailablePlugins` surfaces a chat-listed plugin with an **empty** `usageExamples` array and a populated `slashCommand` — the signal to the supervisor that it must compose `{slashCommand} {target}`. (Internally the catalog still synthesises a single tuning-only usage example so `PluginInputResolver` can carry the `model` / `max-budget-usd` / … onto the dispatch; that example has no prompt and no inputs and is filtered out of the model-facing list.)

### Catalog selection rule (chat-exclusive, else webhook fallback)

`AvailablePluginsCatalog` builds one catalog entry per unique `plugin-name@marketplace`, and for each plugin applies this rule:

- If the plugin is listed by **any chat rule set**, the catalog serves it with that rule set's tuning exclusively — the webhook rule-set usage examples are not shown to the chat tool at all.
- Otherwise (no chat rule set lists that plugin), the catalog falls back to the plugin's webhook usage examples (with their `execute-prompt` templates and `use-inputs`) — so tenants who haven't authored a chat rule set for a plugin see no behaviour change.

---

## 1b. `platform` & `repository` — Structural execution context

These two execution-level fields describe **what the run operates on** — independent of which plugin is used. They're resolved before any plugin runs, used by the framework itself (credential setup, workspace volume, worktree checkout, chat-side input resolution), **and** auto-injected into `XIANIX_INPUTS` under canonical kebab-case keys so plugin prompts and the executor entrypoint can read them off the same keys they always have.

```json
"platform": "github",
"repository": "repository.clone_url"
```

The bare-string form is shorthand for `{ "url": "repository.clone_url" }`. The object form is still accepted when you need a constant URL or an explicit `name`:

```json
"platform": "github",
"repository": {
  "url": "repository.clone_url"
}
```

| Field             | Type                                                                | Description |
|-------------------|---------------------------------------------------------------------|-------------|
| `platform`        | string literal                                                      | Hosting service (`github`, `azuredevops`, …). Used by the executor to pick the right `git` credential helper and is exposed to plugin prompts as `{{platform}}`. Empty / omitted means the executor will infer from the repo URL (defaults to `github`). |
| `repository`      | string (JSON path) **or** object                                    | Either a bare JSON path for the clone URL (shorthand for `repository.url`) or an object with `url` / optional `name`. |
| `repository.url`  | string (JSON path) **or** `{ value, constant }` object              | Either a JSON path that resolves to the clone URL (the common webhook-driven case) or a hard-coded literal via the constant form (see below). **Mandatory when declared** — if a declared JSON path doesn't resolve, the execution block is skipped before any container starts. Exposed as `{{repository-url}}`. |

> **`{{repository-name}}` is derived, not declared.** A short `owner/repo`-style identifier is computed from the resolved `repository.url` (platform-aware: GitHub, Azure DevOps `_git` URLs, etc.) and auto-injected as `{{repository-name}}`. There is no `repository.name` knob in the schema — clone URL and display name are kept in lockstep so they can never drift.
>
> If you need a different display name, pick a different clone URL — that's the single source of truth.

#### Hard-coding the repository (constant form)

For runs whose repository is fixed regardless of the webhook payload — cron pings, Slack triggers, single-tenant agents pinned to one repo, manual triggers — wrap the value in `{ "value": "...", "constant": true }`:

```jsonc
"repository": {
  "url": { "value": "https://github.com/my-org/agent-target.git", "constant": true }
}
```

The bare-string shorthand (`"url": "repository.clone_url"`) is just sugar for `{ "value": "repository.clone_url", "constant": false }`, so existing rules need no changes.

Constant URLs of course also drive `{{repository-name}}` — `RepositoryNaming.DeriveName` runs on the resolved URL regardless of how it was supplied.

### Why split these out from `use-inputs`?

- They are **structural** — every webhook-triggered run on a repo needs them, regardless of plugin. Promoting them to execution-level removes per-plugin duplication and makes the contract explicit.
- The framework needs them **before** the plugin loop runs (clone target, credential helper, volume name) — they were already special-cased; now the schema reflects that.
- The chat-driven path (`SupervisorSubagentTools.RunClaudeCodeOnRepository`) treats `RepositoryUrl` / `RepositoryName` as first-class typed fields and derives the name from the URL the same way the webhook path does, via `RepositoryNaming.DeriveName`. Aligning the webhook schema removes a subtle divergence.
- Executions that don't operate on a repo (e.g. Azure DevOps work-item analysis) just **omit** the `repository` block — no need for `mandatory: false` ceremony on per-plugin inputs.
- The worktree always starts on the **default-branch HEAD**. Task-specific refs (PR head, feature branch, tag) are resolved by the plugin from the prompt.

### Wire-format

Plugin prompts and `Executor/entrypoint.sh` always read structural values from these canonical `XIANIX_INPUTS` keys (`platform`, `repository-url`, `repository-name`). The agent serialises the resolved structural values into the inputs dict under exactly these keys — they are **not** authored under `use-inputs` and the same key names are not used for anything else. `repository-name` is the derived value (from `repository.url`), not a separate path.

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

| Operator | Meaning       | Case-sensitive | Missing path returns |
|----------|---------------|----------------|----------------------|
| `==`     | Equals              | yes | `false` |
| `!=`     | Not equals          | yes | `true`  |
| `^=`     | Starts with (prefix) | no  | `false` |
| `!^=`    | Does not start with  | no  | `true`  |
| `*=`     | Contains (substring) | no  | `false` |
| `!*=`    | Does not contain     | no  | `true`  |
| `?`      | Path exists and is non-null | n/a | `false` |
| `!?`     | Path missing or null | n/a | `true` |

The text-search operators (`^=`/`!^=` and `*=`/`!*=`) match **case-insensitively** — they are meant for fuzzy human text such as `@`-mentions and message bodies (e.g. `comment.body*='@xianix'` matches `@Xianix`). Equality (`==`/`!=`) stays **case-sensitive** because it targets structured identifiers where case is meaningful (GitHub label and branch names, enum-like statuses).

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

> **Don't put structural context here.** `platform`, `repository-url`, and `repository-name` are declared at the [execution level](#1b-platform--repository--structural-execution-context) and auto-injected into `XIANIX_INPUTS` for you. Authoring them under `use-inputs` is unsupported — the framework uses the structural fields for credential setup, volume management, and chat-side input validation.

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
| --------------- | ---------------- |
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

- **Every rule-set-level `with-envs` entry** — applied unconditionally, regardless of platform, from **both** webhook rule sets and chat rule sets (a chat rule set has only rule-set-level `with-envs`, no executions). This is precisely the "common defaults" contract: a `GITHUB-TOKEN` declared at the rule-set level is available to chat runs the same way it's available to every execution.
- **Per-execution `with-envs` entries** (webhook rule sets only) whose execution matches the chosen repository's platform (or is platform-agnostic) — kept under the platform filter so a GitHub-targeted chat run doesn't inherit Azure DevOps's mandatory PAT and vice versa.

Both lists are then deduped by env name. The platform filter intentionally does *not* apply to rule-set commons — if you want a credential to be platform-specific, declare it under the matching execution(s), not at the rule-set level.

### Resolution precedence

When the host `.env` (or Key Vault on the deployed VM) declares `ANTHROPIC-API-KEY`, that value is seeded into the executor container as a default. If the host does **not** declare it, no seed is emitted and the container is expected to receive the key from a `with-envs` entry in `rules.json` instead. All CM platform credentials — `GITHUB-TOKEN`, `AZURE-DEVOPS-TOKEN`, anything else — are **not** read from the host: each tenant must store their own in the Xians Secret Vault and reference it from `rules.json` via `"value": "secrets.<KEY>"`. `with-envs` entries are layered on top of the host-derived defaults at container-start time, so any `secrets.*` or `host.*` entry in `rules.json` overrides whatever was seeded.

### Agent-process credentials (e.g. `ANTHROPIC-API-KEY`)

Some credentials are consumed by the agent process itself, not just the container — `ANTHROPIC-API-KEY` is the headline example (the supervisor and Rules Optimizer chat agents call Claude directly). Those chat agents are constructed at process start from the host env (`EnvConfig.AnthropicApiKey`). An empty host value fails those constructors.

Executor containers still honour rule-set-level `with-envs` for `ANTHROPIC-API-KEY` (and other keys) via the normal merge: `"constant": true`, `"value": "host.VAR_NAME"`, or `"value": "secrets.SECRET-KEY"` against the tenant vault. The host env is seeded into the container when present; a `with-envs` entry overrides it.

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

## 7. `raise-events` — Outbound execution notifications

After an execution finishes, `raise-events` notifies **external listeners** that the run happened and what it cost — an AI Hub node, a billing pipeline, a Slack relay. Each entry is one HTTP `POST`.

It is declared **per execution**, alongside `execute-prompt`. That keeps the scope unambiguous — an entry covers exactly the block it sits on, so there's no separate trigger list or `match-any` filter deciding which runs are in scope — and it means the placeholders resolve against the same values the prompt already used. When several execution blocks match one payload, each one raises its own events, exactly as each starts its own container.

```jsonc
"raise-events": [
  {
    "name": "ai-hub-metrics",
    "url": "https://app-ai-hub-api-h3c5cwascnetb2cj.northeurope-01.azurewebsites.net/nodes/nd_blscMVsoz0?activity=pr-review&corelationid={{pr-number}}&actors=xianix-pr-reviewer",
    "with-headers": [
      { "name": "Authorization", "value": "secrets.AIHUB-API-KEY", "mandatory": true }
    ],
    "payload": {
      "dimensions": {
        "tokens":  "{{metrics.tokens.total}}",
        "costUsd": "{{metrics.cost-usd}}",
        "model":   "{{metrics.model}}",
        "status":  "{{metrics.status}}"
      }
    }
  }
]
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | No | Label for logs and delivery-failure reasons, like the `name` on `match-any` / `use-inputs` / `with-envs` entries. |
| `url` | Yes | Listener endpoint. Interpolated (see below), so run data can be carried in the path or query string. |
| `with-headers` | No | Request headers, using the **same entry shape as [`with-envs`](#5-with-envs--container-environment-variables)** (`name` / `value` / `constant` / `mandatory`) — so `secrets.*`, `host.*`, and `constant` literals resolve identically. This is how authorization is declared: name the header explicitly rather than leaving the scheme implicit. |
| `payload` | No | JSON body, sent as-is apart from interpolation. Arbitrarily nested — the listener's own schema dictates the shape. Omit for endpoints that take everything in the URL. |

Several entries mean several independent listeners: each is delivered on its own, and one failing endpoint doesn't suppress the others.

### Interpolation

`{{...}}` means the same thing it does in `execute-prompt` — resolved `use-inputs` names, plus the auto-injected structural keys `platform`, `repository-url`, and `repository-name`. Nothing new to learn: if the prompt can reference `{{pr-number}}`, so can an event.

The one addition is the `metrics.*` namespace, which carries what the run *produced* rather than what triggered it (cost, tokens, model, status). Inputs are known before the container starts; metrics only exist once it has finished — which is why events are raised **after** the execution completes.

> **`metrics.*` is not implemented yet.** The names above are the intended shape, not a live contract. Until it lands, keep events on `use-inputs` and structural values.

Two rules keep the JSON unambiguous:

- **In `payload`, a bare string is a literal; only `{{...}}` is a reference.** So `"activity": "pr-review"` sends that text and needs no `constant: true` flag (unlike `use-inputs` and `with-envs`, where the `value` slot is a path by default). Mixed content works too: `"summary": "{{repository-name}} #{{pr-number}}"`.
- **A value that is a single `{{...}}` reference keeps its native JSON type**; mixed content is always a string. This is what lets `costUsd` arrive as the number `0.42` rather than `"0.42"`.

Values interpolated into `url` are **percent-encoded**, since a title or branch name containing `&` or `=` would otherwise corrupt the query string. Values in `payload` are JSON-escaped only.

When a reference doesn't resolve, the key is **omitted** from `payload` and interpolates as an empty string in `url` — a `null` cost can't be sent into a numeric field, and an absent dimension is easier for a listener to handle than an empty one.

### Delivery

Notifications are **best-effort and never affect the run**: the execution's success, its metrics, and its logs are already final by the time an event is raised, so a slow or broken listener cannot fail or delay the work. Delivery is bounded by a timeout with limited retries, and a permanent failure is logged against the entry's `name` and dropped.

Only the fields declared here are transmitted. Be deliberate about that — a listener is a third-party endpoint, and interpolating repository content or prompt text sends tenant data off-platform.

### Known gaps

- **Chat runs.** A chat rule set has no `executions`, so chat-initiated runs have nowhere to declare an event. Webhook and schedule rule sets both carry executions, so both work unchanged.
- **Repetition across blocks.** An entry is per execution, so several blocks reporting to one listener repeat the `url` and headers. If that becomes a burden, the natural fix is the two-level merge `with-envs` already uses (rule-set-level defaults, execution-level overrides by `name`) rather than a separate root-level event rule set.

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
          "url": "repository.clone_url"
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
        "execute-prompt": "You are reviewing pull request #{{pr-number}} titled \"{{pr-title}}\" in the repository {{repository-name}}.\n\nRun /pr-review {{pr-number}} to perform the automated review. The `gh` CLI is authenticated and available if you need it directly.",
        "raise-events": [
          {
            "name": "ai-hub-metrics",
            "url": "https://app-ai-hub-api-h3c5cwascnetb2cj.northeurope-01.azurewebsites.net/nodes/nd_blscMVsoz0?activity=pr-review&corelationid={{pr-number}}&actors=xianix-pr-reviewer",
            "with-headers": [
              { "name": "Authorization", "value": "secrets.AIHUB-API-KEY", "mandatory": true }
            ],
            "payload": {
              "dimensions": {
                "tokens": "{{metrics.tokens.total}}",
                "costUsd": "{{metrics.cost-usd}}",
                "model": "{{metrics.model}}",
                "status": "{{metrics.status}}"
              }
            }
          }
        ]
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
3. The structural fields (`platform`, `repository.url`) are resolved alongside `use-inputs`. Any declared structural field that fails to resolve **skips the block** with a clear error — same code path as a missing mandatory input.
4. The resolved structural values are auto-injected into the inputs dictionary as `platform` and `repository-url`. The short `repository-name` (e.g. `owner/repo`) is **derived** from `repository-url` via `RepositoryNaming.DeriveName` (platform-aware: handles GitHub, Azure DevOps `_git` URLs, etc.) and injected alongside them — these are the canonical wire-format keys plugin prompts and the executor entrypoint expect.
5. `execute-prompt` is interpolated against the merged inputs dict.
6. The agent merges rule-set-level common `with-envs` with the matched execution's own `with-envs` (execution-level entries override rule-set entries by env name), resolves each entry (literals, `host.*`, `secrets.*`), and injects them into the executor container alongside the runtime values it manages itself.
7. The executor uses `platform` to pick the right credential helper, `git clone`s `repository-url` into the per-tenant workspace volume, checks out the default-branch HEAD into the per-run worktree, installs `use-plugins`, and runs the prompt. Plugins perform any further task-specific checkout themselves.
8. Once the run has finished and its metrics are recorded, each `raise-events` entry on the matched block is delivered to its listener — best-effort, so a failed notification is logged and dropped without touching the run's outcome.

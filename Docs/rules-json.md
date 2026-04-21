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
    "executions": [
      {
        "name": "...",
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
| `executions` | One or more execution blocks |

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

```json
"use-inputs": [
  { "name": "pr-number",      "value": "number",                "mandatory": true },
  { "name": "repository-url", "value": "repository.clone_url",  "mandatory": true },
  { "name": "platform",       "value": "github", "constant": true }
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

Declares environment variables to inject into the executor container before the prompt runs. Sits at the **execution-block** level (sibling to `use-plugins`) — every variable is available to every plugin and to the prompt itself, regardless of how many plugins consume it.

```json
"with-envs": [
  { "name": "GITHUB-TOKEN",       "value": "secrets.GITHUB-TOKEN", "mandatory": true },
  { "name": "FEATURE-FLAG-MODE",  "value": "strict",               "constant": true }
]
```

| Field       | Description |
|-------------|-------------|
| `name`      | Env var name inside the container |
| `value`     | One of: `env.VAR_NAME` (read from the **host** environment), `secrets.SECRET-KEY` (fetched from the **tenant Secret Vault** via `XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)` at container-start time), or a literal string when `"constant": true`. |
| `constant`  | *(optional)* Treat `value` as a literal |
| `mandatory` | *(optional, default `false`)* When `true`, the executor container **fails to start** (non-retryable) if this env resolves to `null` or empty. Use for credentials the prompt cannot run without. |

### Resolution precedence

Only `ANTHROPIC-API-KEY` is seeded into the container from the host `.env` (it's also required for the agent process itself). All CM platform credentials — `GITHUB-TOKEN`, `AZURE-DEVOPS-TOKEN`, anything else — are **not** read from the host: each tenant must store their own in the Xians Secret Vault and reference it from `rules.json` via `"value": "secrets.<KEY>"`. `with-envs` entries are layered on top of the host-derived defaults at container-start time, so any `secrets.*` or `env.*` entry in `rules.json` overrides whatever was seeded.

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
    "executions": [
      {
        "name": "github-pull-request-review",
        "match-any": [
          { "name": "pr-opened-event", "rule": "action==opened" }
        ],
        "use-inputs": [
          { "name": "pr-number",       "value": "number" },
          { "name": "repository-url",  "value": "repository.clone_url" },
          { "name": "repository-name", "value": "repository.full_name" },
          { "name": "pr-title",        "value": "pull_request.title" },
          { "name": "pr-head-branch",  "value": "pull_request.head.ref" },
          { "name": "platform",        "value": "github", "constant": true }
        ],
        "use-plugins": [
          {
            "plugin-name": "pr-reviewer@xianix-plugins-official",
            "marketplace": "xianix-team/plugins-official"
          }
        ],
        "with-envs": [
          { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
        ],
        "execute-prompt": "You are reviewing pull request #{{pr-number}} titled \"{{pr-title}}\" in the repository {{repository-name}} (branch: {{pr-head-branch}}).\n\nRun /code-review to perform the automated review. The `gh` CLI is authenticated and available if you need it directly."
      }
    ]
  }
]
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
3. `use-inputs` are resolved from the payload.
4. `execute-prompt` is interpolated.
5. The agent resolves `with-envs` (literals, `env.*`, `secrets.*`) and injects them into the executor container alongside the runtime values it manages itself.
6. The executor installs `use-plugins` and runs the prompt.

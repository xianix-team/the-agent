# Rules Configuration (`rules.json`)

The rules file is the single configuration surface that controls **what the agent does** when a webhook arrives. Each entry in the JSON array is a self-contained rule set that maps a webhook event to a fully executable Claude Code session — complete with payload filtering, input extraction, plugin installation, and a templated prompt.

```
rules.json  →  WebhookRulesEvaluator  →  EventOrchestrator  →  ProcessingWorkflow  →  Executor Container
```

---

## File Structure

`rules.json` is a JSON array of **rule set** objects. Each rule set targets one webhook name and is independent of the others.

```jsonc
[
  {
    "webhook-name": "...",       // 1. Which webhook to handle
    "match": [ ... ],            // 2. When to act (optional filters)
    "inputs": [ ... ],           // 3. What to extract from the payload
    "claude-code-plugins": [...],// 4. Which plugins to install
    "prompt": "..."              // 5. What to tell Claude Code
  }
]
```

### Evaluation flow

```
┌──────────────────────────────────────────────────────────────────────┐
│  Incoming Webhook                                                    │
│  name: "pull requests"   payload: { action: "opened", number: 42 }  │
└───────────────────────────────┬──────────────────────────────────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Find rule set where  │
                    │  webhook-name matches │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Evaluate match rules │──── No match? → skip
                    │  (OR logic)           │
                    └───────────┬───────────┘
                                │ At least one passes
                    ┌───────────▼───────────┐
                    │  Extract inputs from  │
                    │  payload              │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Interpolate prompt   │
                    │  with {{input-name}}  │
                    └───────────┬───────────┘
                                │
                    ┌───────────▼───────────┐
                    │  Start executor with  │
                    │  plugins + prompt     │
                    └───────────────────────┘
```

---

## 1. `webhook-name`

Case-insensitive match against the webhook name configured in Xians Agent Studio.

```json
"webhook-name": "pull requests"
```

Only one rule set per webhook name is used — the **first** match in the array wins.

---

## 2. `match` — Payload Filtering

An array of filter rules evaluated with **OR logic**: the webhook passes if **any** entry matches. If `match` is omitted or empty, the webhook passes unconditionally.

```json
"match": [
  { "name": "pr-opened-event",       "rule": "action==opened" },
  { "name": "pr-synchronize-event",  "rule": "action==synchronize" }
]
```

| Field  | Description |
|--------|-------------|
| `name` | Human-readable label (for logging/debugging) |
| `rule` | A filter expression — see syntax below |

### Filter expression syntax

Each rule is a simple comparison of a **JSON path** against a **literal value**:

```
<json-path> <operator> <expected-value>
```

| Operator | Meaning       | Missing path returns |
|----------|---------------|----------------------|
| `==`     | Equals        | `false`              |
| `!=`     | Not equals    | `true`               |

**JSON paths** use dot-notation to traverse the payload. Given this payload:

```json
{ "action": "opened", "pull_request": { "draft": false } }
```

| Expression                       | Result  |
|----------------------------------|---------|
| `action==opened`                 | `true`  |
| `action!=closed`                 | `true`  |
| `pull_request.draft==false`      | `true`  |
| `action==closed`                 | `false` |

Type coercion is handled automatically — strings, numbers, booleans, and `null` are all compared correctly against the literal on the right-hand side.

---

## 3. `inputs` — Payload Extraction

Extracts values from the webhook payload into named variables. These become available for prompt interpolation and are forwarded to the executor container as the `XIANIX_INPUTS` JSON blob.

```json
"inputs": [
  { "name": "pr-number",      "value": "number" },
  { "name": "repository-url",  "value": "repository.clone_url" },
  { "name": "platform",        "value": "github",  "constant": true }
]
```

| Field      | Description |
|------------|-------------|
| `name`     | Key name in the extracted dictionary |
| `value`    | Dot-separated JSON path into the payload, **or** a literal string when `constant` is `true` |
| `constant` | *(optional, default `false`)* When `true`, `value` is used as-is instead of being resolved as a path |

### Path resolution examples

Given this webhook payload:

```json
{
  "number": 42,
  "repository": { "clone_url": "https://github.com/acme/app.git", "full_name": "acme/app" },
  "pull_request": { "title": "Fix auth bug", "head": { "ref": "fix/auth" } }
}
```

| Input definition | Resolved value |
|---|---|
| `"value": "number"` | `42` |
| `"value": "repository.clone_url"` | `"https://github.com/acme/app.git"` |
| `"value": "pull_request.head.ref"` | `"fix/auth"` |
| `"value": "github", "constant": true` | `"github"` (literal) |

If a path doesn't resolve (the property is missing), the input is set to `null`.

---

## 4. `claude-code-plugins` — Plugin Installation

Declares Claude Code marketplace plugins to install in the executor container before the prompt runs.

```json
"claude-code-plugins": [
  {
    "name": "github",
    "description": "GitHub MCP server — typed GitHub API tools",
    "url": "github@claude-plugins-official",
    "marketplace": "anthropics/claude-plugins-official"
  },
  {
    "name": "pr-reviewer",
    "description": "Comprehensive PR review plugin",
    "url": "pr-reviewer@xianix-plugins-official",
    "marketplace": "xianix-team/plugins-official",
    "envs": [
      { "name": "GITHUB_PERSONAL_ACCESS_TOKEN", "value": "env.GITHUB_TOKEN" }
    ]
  }
]
```

| Field         | Required | Description |
|---------------|----------|-------------|
| `name`        | Yes | Human-readable plugin identifier |
| `description` | No  | What the plugin provides (documentation only) |
| `url`         | Yes | Plugin reference in `plugin-name@marketplace-name` format, passed to `claude plugin install` |
| `marketplace` | No  | Marketplace source to register via `claude plugin marketplace add`. Accepts GitHub `owner/repo`, a git URL, a local path, or a URL to a `marketplace.json`. Omit for the built-in Anthropic marketplace. |
| `envs`        | No  | Environment variables to inject into the container for this plugin |

### Plugin environment variables (`envs`)

Each entry in the `envs` array injects an environment variable into the executor container.

```json
{ "name": "GITHUB_PERSONAL_ACCESS_TOKEN", "value": "env.GITHUB_TOKEN" }
```

| Field      | Description |
|------------|-------------|
| `name`     | The env var name set inside the container |
| `value`    | By default, an `env.VAR_NAME` reference — the `env.` prefix is stripped and the variable is read from the **host** process environment. Set `"constant": true` to use the value as a static literal instead. |
| `constant` | *(optional, default `false`)* Treat `value` as a literal string |

---

## 5. `prompt` — Claude Code Prompt Template

A string template executed as the Claude Code prompt after plugins are installed. Use `{{input-name}}` placeholders to inject extracted input values.

```json
"prompt": "You are reviewing PR #{{pr-number}} titled \"{{pr-title}}\" in {{repository-name}} (branch: {{pr-head-branch}}).\n\nRun /code-review to perform the automated review."
```

Placeholders are replaced at evaluation time (case-insensitive match). Any `{{name}}` that doesn't correspond to a resolved input is left as-is in the output.

---

## Complete Example

Putting it all together — a rule set that reviews newly opened pull requests:

```json
[
  {
    "webhook-name": "pull requests",
    "match": [
      { "name": "pr-opened-event", "rule": "action==opened" }
    ],
    "inputs": [
      { "name": "pr-number",        "value": "number" },
      { "name": "repository-url",   "value": "repository.clone_url" },
      { "name": "repository-name",  "value": "repository.full_name" },
      { "name": "pr-title",         "value": "pull_request.title" },
      { "name": "pr-head-branch",   "value": "pull_request.head.ref" },
      { "name": "platform",         "value": "github", "constant": true }
    ],
    "claude-code-plugins": [
      {
        "name": "github",
        "description": "GitHub MCP server",
        "url": "github@claude-plugins-official",
        "marketplace": "anthropics/claude-plugins-official"
      },
      {
        "name": "pr-reviewer",
        "description": "PR review plugin",
        "url": "pr-reviewer@xianix-plugins-official",
        "marketplace": "xianix-team/plugins-official",
        "envs": [
          { "name": "GITHUB_PERSONAL_ACCESS_TOKEN", "value": "env.GITHUB_TOKEN" }
        ]
      }
    ],
    "prompt": "You are reviewing PR #{{pr-number}} titled \"{{pr-title}}\" in {{repository-name}} (branch: {{pr-head-branch}}).\n\nRun /code-review to perform the automated review. The `gh` CLI is authenticated and available if you need it directly."
  }
]
```

### What happens at runtime

1. A **GitHub `pull_request` webhook** fires with `action: "opened"`
2. The evaluator matches `webhook-name: "pull requests"` and the filter `action==opened` passes
3. Six inputs are extracted (five from the payload, one constant)
4. The prompt template is interpolated with the extracted values
5. The executor container installs `github` and `pr-reviewer` plugins, injects `GITHUB_PERSONAL_ACCESS_TOKEN` from the host's `GITHUB_TOKEN`, and runs the final prompt

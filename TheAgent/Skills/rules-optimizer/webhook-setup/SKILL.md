---
name: webhook-setup
description: CreateWebhookConnection (Default) after permission; then GitHub register/ping or ADO manual URL; end Completed or Failed.
---

# Webhook setup + SCM connection

Follow **context → action → evidence**. **Ask first** — do not create the Xians webhook until the user agrees.
This skill covers Default webhook **and** SCM connection. Do **not** load a separate connection-test skill.

**Precondition:** activation rules must have installed plugins (from prior verify).

---

## Part A — Xians Default webhook

### Context

1. Call `GetTenantState` (silent) — reuse existing Default webhook if already present.
2. Briefly restate install success (one line) if just confirmed.
3. Tell the user how to trigger each installed plugin **before** the webhook question.
   Prefer the label/trigger they already chose. Otherwise call `ListAvailablePlugins` with the configured platform for `suggestedTriggers` only — never invent.

```
pr-reviewer is installed and saved to rules.json.

How to trigger on GitHub:
- Add the label `ai-dlc/pr/pr-review` to a pull request (or open a PR that already has it). New commits on a labeled PR also re-run the review. You can also mention `@xianix` in a PR comment.

Create the Xians webhook (Default) for this activation now?
```

For Azure DevOps, use ADO wording from `suggestedTriggers` — **not** GitHub label names.

If `GetTenantState.webhooks.items` already has Default with a URL, say it exists and ask whether to reuse it (still call `CreateWebhookConnection` to reuse/ensure — do not invent the URL).

### Action

4. If **no** → acknowledge; stop (skip Part B). Keep trigger instructions as the takeaway. Final line: `8. Setup: ❌ Failed — webhook declined` only if they abandon setup; otherwise leave webhook pending without claiming completed.
5. If **yes** → call `CreateWebhookConnection` with `webhookName` **`Default`**.

### Evidence

6. Report from tool fields only — **full details**:
   - failed → `6. Create Xians webhook: ❌ Failed — {error}`
   - created/reused → show:

```
6. Create Xians webhook: ✅

- Name: {webhookName}
- URL: [{webhookUrl}]({webhookUrl})
- Integration id: {integrationId}
- Agent: {agentName} / {activationName}
```

7. Do **not** claim the SCM connection is ready until Part B evidence says so.

### Next

On verified create → continue **Part B** in this skill (same turn when possible).

---

## Part B — SCM connection

Never claim connected unless tools say so.
Call `GetTenantState` first if webhook URL / repo URL are not already known this turn.

### GitHub

#### Context

1. Call `GetTenantState` if webhook URL / repo URL are not already known this turn.
2. **Auto-check** `CheckTenantSecretExists("GITHUB-TOKEN")` before registering.
   - If `GITHUB-TOKEN` is `exists: false` → do **not** ask whether they have it. Say only:

```
GITHUB-TOKEN is missing. Add it in Studio → Settings → Secrets (exact key name), then say "done".
```

   - On "done", re-check; only then continue.
3. Repo URL, webhook URL from prior create (or `GetTenantState.webhooks`), and `events` =
   typically `issues,pull_request,issue_comment,push`. Never use event `label`.

#### Action

Call `RegisterGitHubRepositoryWebhook` **silently** (only after GITHUB-TOKEN exists).

**Forbidden narration** (never say these before/during the tool call):
- "Now registering this webhook with GitHub…"
- "Testing the connection…"
- "Setting up the GitHub webhook…"

The user must only see the **evidence line after** the tool returns.

#### Evidence

Report from tool fields only — examples:

**Success** (`registrationStatus=registered` + `connectionStatus=established`):

```
7. Connect SCM: ✅ Established — ping succeeded on {owner/repo} (HTTP {lastResponseCode}), events: {events}.

8. Setup: ✅ Completed
```

**Registered but ping failed:**

```
7. Connect SCM: ❌ Not established — ping failed: {error}.

8. Setup: ❌ Failed — GitHub connection not established
```

**Registration failed** (including missing token — use `userFacingMessage` if present):

```
7. Connect SCM: ❌ Not established — registration failed: {error}.

8. Setup: ❌ Failed — GitHub webhook registration failed
```

Never claim ready unless `connectionStatus=established`.

### Azure DevOps

#### Context / Action

There is **no** tool that creates Service Hooks. Do **not** call `RegisterGitHubRepositoryWebhook`. Do **not** ping.

After `CreateWebhookConnection`, show the real `webhookUrl` as a markdown link plus the other webhook details and ask the user to create the subscription:

```
7. Connect SCM: Azure DevOps Service Hook (manual)

Webhook details:
- Name: {webhookName}
- Webhook URL: [{webhookUrl}]({webhookUrl})
- Integration id: {integrationId}
- Agent: {agentName} / {activationName}

Create the connection in Azure DevOps:
1. Project settings → Service hooks → + Create subscription
2. Service: Web Hooks
3. Events for installed plugins (e.g. pr-reviewer: Pull request created, Pull request updated)
4. Action URL = the webhook URL above · HTTP POST · Resource details = All
5. Finish

Tell me when you've created it (optional) — I won't validate from here.
```

#### Evidence

Do **not** claim the connection is established. No invented ping or "HTTP 200".

### How to trigger (platform-specific)

Context: `ListAvailablePlugins` with the configured platform if needed. For each installed plugin, show **How to trigger** from that platform's `suggestedTriggers` only. Never invent. Never show the other platform's labels/tags.

### Final status (mandatory)

End every finished setup run with exactly one of:

- `8. Setup: ✅ Completed`
- `8. Setup: ❌ Failed — {short reason from evidence}`

One short closing line: they can add/remove plugins anytime.

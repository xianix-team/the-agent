---
name: connection-test
description: Register/ping (GitHub) or manual URL handoff (ADO); evidence from tool fields; end with Completed or Failed.
---

# Connection

Follow **context → action → evidence**. Never claim connected unless tools say so.
Call `GetTenantState` first if webhook URL / repo URL are not already known this turn.

## GitHub

### Context

1. Call `GetTenantState` if webhook URL / repo URL are not already known this turn.
2. **Auto-check** `CheckTenantSecretExists("GITHUB-TOKEN")` and `CheckTenantSecretExists("GITHUB-WEBHOOK-SECRET")` before registering.
   - If `GITHUB-TOKEN` is `exists: false` → do **not** ask whether they have it. Say only:

```
GITHUB-TOKEN is missing. Add it in Studio → Settings → Secrets (exact key name), then say "done".
```

   - On "done", re-check; only then continue.
   - If `GITHUB-WEBHOOK-SECRET` is `exists: false`, ask the user to add it in Studio → Settings → Secrets (same value GitHub will use as the webhook secret). Re-check on "done" before registering.
3. Repo URL, webhook URL from prior create (or `GetTenantState.webhooks`), and `events` = union of `suggestedGitHubWebhookEvents` for installed plugins (default `issues,pull_request,issue_comment,push`). Never use event `label`.

### Action

Call `RegisterGitHubRepositoryWebhook` **silently** (only after GITHUB-TOKEN and GITHUB-WEBHOOK-SECRET exist).

**Forbidden narration** (never say these before/during the tool call):
- "Now registering this webhook with GitHub…"
- "Testing the connection…"
- "Setting up the GitHub webhook…"

The user must only see the **evidence line after** the tool returns.

### Evidence

Report from tool fields only — examples:

**Success** (`registrationStatus=registered` + `connectionStatus=established`):

1. If the tool returns `rulesVerificationSecret`, call `GetCurrentRules`. When the Default webhook block lacks `github-webhook-verification-secret` → `GITHUB-WEBHOOK-SECRET`, call `SaveRules` to add it, then re-read with `GetCurrentRules` before the final setup line.
2. Then report:

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

## Azure DevOps

### Context / Action

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

### Evidence

Do **not** claim the connection is established. No invented ping or "HTTP 200".

## How to trigger (platform-specific)

Context: `ListAvailablePlugins` with the configured platform if needed. For each installed plugin, show **How to trigger** from that platform's `suggestedTriggers` only. Never invent. Never show the other platform's labels/tags.

## Final status (mandatory)

End every finished setup run with exactly one of:

- `8. Setup: ✅ Completed`
- `8. Setup: ❌ Failed — {short reason from evidence}`

One short closing line: they can add/remove plugins anytime.

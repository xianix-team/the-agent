---
name: connection-test
description: Action register/ping (GitHub) or manual URL handoff (ADO); verify only from tool fields.
---

# Connection

Follow **context → action → verify**. Never claim connected unless tools say so.

## GitHub

### Context

Repo URL, webhook URL from prior create, and `events` = union of `suggestedGitHubWebhookEvents` for installed plugins (default `issues,pull_request,issue_comment,push`). Never use event `label`.

### Action

Call `RegisterGitHubRepositoryWebhook`.

### Verify

Report from tool fields only:

- `registrationStatus=registered` + `connectionStatus=established` → `GitHub connection: ✅ Established — ping succeeded on {repo} (HTTP {lastResponseCode}), events: {events}.`
- registered but not established → `GitHub connection: ❌ Not established — ping failed: {error}.`
- registration failed → `GitHub connection: ❌ Not established — registration failed: {error}.`

Never claim ready unless `connectionStatus=established`.

## Azure DevOps

### Context / Action

There is **no** tool that creates Service Hooks. Do **not** call `RegisterGitHubRepositoryWebhook`. Do **not** ping.

After `CreateWebhookConnection`, show the real `webhookUrl` as a markdown link and ask the user to create the subscription:

```
Azure DevOps Service Hook (manual)

Webhook URL:
{webhookUrl}

Create the connection in Azure DevOps:
1. Project settings → Service hooks → + Create subscription
2. Service: Web Hooks
3. Events for installed plugins (e.g. pr-reviewer: Pull request created, Pull request updated)
4. Action URL = the webhook URL above · HTTP POST · Resource details = All
5. Finish

Tell me when you've created it (optional) — I won't validate from here.
```

### Verify

Do **not** claim the connection is established. No invented ping or "HTTP 200".

## How to trigger (platform-specific)

Context: `ListAvailablePlugins` with the configured platform if needed. For each installed plugin, show **How to trigger** from that platform's `suggestedTriggers` only. Never invent. Never show the other platform's labels/tags.

One short closing line: they can add/remove plugins anytime.

---
name: connection-test
description: GitHub — register and ping. Azure DevOps — show webhook URL and ask the user to create Service Hooks (no validation).
---

# Connection

**Knowledge in scope:** GitHub register/ping, or ADO manual URL handoff. Never claim connected unless tools say so.

## GitHub

Call `RegisterGitHubRepositoryWebhook` with repo URL, webhook URL, and `events` = union of `suggestedGitHubWebhookEvents` for installed plugins (default `issues,pull_request,issue_comment,push`). Never use event `label`.

Report from tool fields only:

- `registrationStatus=registered` + `connectionStatus=established` → `GitHub connection: ✅ Established — ping succeeded on {repo} (HTTP {lastResponseCode}), events: {events}.`
- registered but not established → `GitHub connection: ❌ Not established — ping failed: {error}.`
- registration failed → `GitHub connection: ❌ Not established — registration failed: {error}.`

Never claim ready unless `connectionStatus=established`.

## Azure DevOps

There is **no** tool that creates Service Hooks. Do **not** call `RegisterGitHubRepositoryWebhook`. Do **not** ping. Do **not** validate. Do **not** say the connection is established.

After `CreateWebhookConnection`, show the real `webhookUrl` as a markdown link and ask the user to create the subscription themselves:

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

Keep it short. Always include the full `webhookUrl`. Do not invent a ping or "HTTP 200".

## How to trigger (platform-specific)

Call `ListAvailablePlugins` with the configured platform if needed. For each installed plugin, show **How to trigger** from that platform's `suggestedTriggers` only:

- GitHub → labels (and any other suggested GitHub triggers)
- Azure DevOps → ADO event wording from `suggestedTriggers` (often PR created / updated / reviewer — **not** GitHub label names)

Never invent triggers. Never show the other platform's labels/tags.

One short closing line: they can add/remove plugins anytime.

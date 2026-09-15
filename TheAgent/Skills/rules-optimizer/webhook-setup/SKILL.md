---
name: webhook-setup
description: CreateWebhookConnection (Default) after permission; then GitHub or ADO manual SCM URL; end Completed or Failed.
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
   Prefer the match-any / triggers the user already confirmed in `plugin-setup`. If
   needed, re-read `GetCurrentRules` for saved execution names and match-any — never invent.

```
pr-reviewer is installed and saved to rules.json.

How to trigger on GitHub:
- Add the label `ai-dlc/pr/pr-review` to a pull request (or open a PR that already has it). New commits on a labeled PR also re-run the review. You can also mention `@xianix` in a PR comment.

Create the Xians webhook (Default) for this activation now?
```

For Azure DevOps, use ADO wording from the agreed setup — **not** GitHub label names.

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

Never claim connected unless the user says they created the SCM hook.
There is **no** tool that creates GitHub repo webhooks or Azure DevOps Service Hooks.
Do **not** invent a register/ping tool. Do **not** ping.

Call `GetTenantState` first if webhook URL / repo URL are not already known this turn.

### GitHub

#### Context / Action

After `CreateWebhookConnection`, show the real `webhookUrl` as a markdown link plus the other webhook details and ask the user to create the repo webhook:

```
7. Connect SCM: GitHub webhook (manual)

Webhook details:
- Name: {webhookName}
- Webhook URL: [{webhookUrl}]({webhookUrl})
- Integration id: {integrationId}
- Agent: {agentName} / {activationName}

Create the webhook in GitHub:
1. Repo → Settings → Webhooks → Add webhook
2. Payload URL = the webhook URL above · Content type = application/json
3. Events for installed plugins (typically Issues, Pull requests, Issue comments, Pushes — never invent; use what plugins need)
4. Optional: set a Secret and store the same value in Studio as GITHUB-WEBHOOK-SECRET if rules.json uses github-webhook-verification-secret
5. Add webhook

Tell me when you've created it (optional) — I won't validate from here.
```

#### Evidence

Do **not** claim the connection is established. No invented ping or "HTTP 200".

### Azure DevOps

#### Context / Action

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

Context: restate **How to trigger** from the match-any the user confirmed in
`plugin-setup` (or from saved executions in `GetCurrentRules`). Never invent. Never
show the other platform's labels/tags.

### Final status (mandatory)

End every finished setup run with exactly one of:

- `8. Setup: ✅ Completed` — after Xians webhook create succeeded and SCM instructions were shown (manual SCM is expected; do not wait for validation)
- `8. Setup: ❌ Failed — {short reason from evidence}`

One short closing line: they can add/remove plugins anytime.

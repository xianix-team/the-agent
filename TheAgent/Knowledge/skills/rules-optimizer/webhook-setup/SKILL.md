---
name: webhook-setup
description: GetTenantState; CreateWebhookConnection (Default) after permission; show full webhook details.
---

# Webhook setup

Follow **context → action → evidence**. **Ask first** — do not create until the user agrees.

**Precondition:** activation rules must have installed plugins (from prior verify).

## Context

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

## Action

4. If **no** → acknowledge; stop (skip connection-test). Keep trigger instructions as the takeaway. Final line: `8. Setup: ❌ Failed — webhook declined` only if they abandon setup; otherwise leave webhook pending without claiming completed.
5. If **yes** → call `CreateWebhookConnection` with `webhookName` **`Default`**.

## Evidence

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

7. Do **not** claim the SCM connection is ready. `scmConnectionStatus` stays `not_established` until `connection-test`.

## Next

On verified create → load `connection-test` (silently).

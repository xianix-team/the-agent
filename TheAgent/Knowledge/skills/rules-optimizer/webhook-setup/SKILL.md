---
name: webhook-setup
description: Context triggers; action CreateWebhookConnection after permission; verify tool success.
---

# Webhook setup

Follow **context → action → verify**. **Ask first** — do not create until the user agrees.

**Precondition:** activation rules must have installed plugins (from prior verify).

## Context

1. Briefly restate install success (one line) if just confirmed.
2. Tell the user how to trigger each installed plugin **before** the webhook question.
   Prefer the label/trigger they already chose. Otherwise call `ListAvailablePlugins` with the configured platform for `suggestedTriggers` only — never invent.

```
pr-reviewer is installed and saved to rules.json.

How to trigger on GitHub:
- Add the label `ai-dlc/pr/pr-review` to a pull request (or open a PR that already has it). New commits on a labeled PR also re-run the review. You can also mention `@xianix` in a PR comment.

Create the Xians webhook for this activation now?
```

For Azure DevOps, use ADO wording from `suggestedTriggers` — **not** GitHub label names.

## Action

3. If **no** → acknowledge; stop (skip connection-test). Keep trigger instructions as the takeaway.
4. If **yes** → call `CreateWebhookConnection` (name `Default` unless changed).

## Verify

5. Report from tool fields only:
   - failed → `Xians webhook: ❌ Failed — {error}`
   - created → `Xians webhook: ✅ Created — {webhookUrl}`
6. Do **not** claim the SCM connection is ready. `scmConnectionStatus` stays `not_established` until `connection-test`.

## Next

On verified create → load `connection-test` (silently).

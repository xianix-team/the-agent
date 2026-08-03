---
name: webhook-setup
description: After rules are saved, tell the user how to trigger installed plugins, then ask permission to create the Xians webhook. Do not register/ping GitHub here — that is connection-test.
---

# Webhook setup

**When:** after successful `InstallPlugins` / save. **Ask first** — do not create until the user agrees.

**Precondition:** activation rules must have installed plugins.

1. If you just confirmed install, briefly restate success (one line). Then **always** tell the user how to trigger each newly installed plugin **before** the webhook question.

   Call `ListAvailablePlugins` with the configured platform if `suggestedTriggers` are not already in context. Use that platform's `suggestedTriggers` only — never invent labels/tags.

   Example shape (adapt to real `suggestedTriggers`):

```
pr-reviewer is installed and saved to rules.json.

How to trigger on GitHub:
- Add the label `ai-dlc/pr/pr-review` to a pull request (or open a PR that already has it). New commits on a labeled PR also re-run the review. You can also mention `@xianix` in a PR comment.

Create the Xians webhook for this activation now?
```

   For Azure DevOps, use ADO wording from `suggestedTriggers` (PR created / source branch updated / agent as reviewer / `@xianix` comment) — **not** GitHub label names.

2. Ask once (include the trigger blurb in the **same** message as the ask):

```
Create the Xians webhook for this activation now?
```

3. If **no** → acknowledge; stop (skip connection-test). Still leave the trigger instructions above as the user's takeaway.
4. If **yes** → call `CreateWebhookConnection` (name `Default` unless changed).
5. Report one line:
   - failed → `Xians webhook: ❌ Failed — {error}`
   - created → `Xians webhook: ✅ Created — {webhookUrl}`
6. Do **not** claim the SCM connection is ready. `scmConnectionStatus` stays `not_established` until `connection-test`.

**Azure DevOps:** after create, `connection-test` only shows the URL and asks the user to create Service Hooks — no ping/validation.

## Next

On successful create → load `connection-test` (silently — never mention skills).

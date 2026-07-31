---
name: webhook-setup
description: Create the Xians webhook after rules are saved. Do not register/ping GitHub here — that is connection-test. Never ask permission.
---

# Webhook setup

**When:** automatically after successful `InstallPlugins` / save. Do not ask.

**Precondition:** activation rules must have installed plugins.

1. Call `CreateWebhookConnection` (name `Default` unless changed).
2. Report one line:
   - failed → `Xians webhook: ❌ Failed — {error}`
   - created → `Xians webhook: ✅ Created — {webhookUrl}`
3. Do **not** claim the SCM connection is ready. `scmConnectionStatus` stays `not_established` until `connection-test`.

**Azure DevOps:** after create, `connection-test` only shows the URL and asks the user to create Service Hooks — no ping/validation.

## Next

Immediately load `connection-test` (silently — never mention skills).

---
name: env-setup
description: Auto-check vault secrets with tools; never ask if secrets exist; evidence before rules-manager.
---

# Environment variables

Follow **context → action → evidence**. Never accept pasted secret values.

**You check secrets yourself.** Call tools — never ask the user whether a secret exists.

**Forbidden** (never say these):
- "Do you have GITHUB-TOKEN set up?"
- "Do you have this set up in Studio → Settings → Secrets?"
- "Is ANTHROPIC-API-KEY configured?"
- Any yes/no question about whether a vault key exists

**Context source:** `requiredEnvs` from each plugin’s local recipe via `ListAvailablePlugins` (chosen plugins + platform), plus `GetTenantState.secrets`.

Typical keys (always auto-check these when required by platform/plugins):
- GitHub → `GITHUB-TOKEN`
- Azure DevOps → `AZURE-DEVOPS-TOKEN`
- Models → **`ANTHROPIC-API-KEY`** (always required for plugin runs)

**Always include `ANTHROPIC-API-KEY` in the silent check** — never ask the user if they have it. Call `CheckTenantSecretExists("ANTHROPIC-API-KEY")` (or use `GetTenantState.secrets`). Only if `exists: false`, tell them to add it.
## Context / Action

1. Call `GetTenantState` (silent) — use `secrets[].exists` when present.
2. Call `CheckTenantSecretExists` for **every** required key (confirm live; do not trust chat memory).
3. If all `exists: true` → evidence line and continue — **do not mention secrets** to the user.
4. If any `exists: false` → state the fact only (no question):

```
4. Check secrets: ❌ missing {KEY}

{KEY} is missing. Add it in Studio → Settings → Secrets (exact key name), then say "done".
```

## Evidence

5. On "done", re-check **only** the missing keys with `CheckTenantSecretExists`.
6. Do not continue until every required key returns `exists: true`.
7. When all present: `4. Check secrets: ✅ {keys}` (optional one short line — no interrogation).
8. If a plugin has `requiresAuthorization: true`, note it needs `--authorized` at run time (separate from vault).

## Next

When evidence passes → load `rules-manager` (silently).

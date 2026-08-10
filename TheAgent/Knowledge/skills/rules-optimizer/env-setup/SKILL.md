---
name: env-setup
description: Context/check vault secrets; verify all required keys exist before rules-manager.
---

# Environment variables

Follow **context → action → verify**. Never ask whether secrets are needed. Never accept pasted secret values.

**Context source:** `requiredEnvs` from each plugin’s local recipe via `ListAvailablePlugins` (chosen plugins + platform).

Typical keys: GitHub → `GITHUB-TOKEN`; Azure DevOps → `AZURE-DEVOPS-TOKEN`; models → `ANTHROPIC-API-KEY`.

## Context / Action

1. Call `CheckTenantSecretExists` for each required key.
2. If any `exists: false`, tell them to add those exact keys in Studio → Settings → Secrets, then say "done".

## Verify

3. On "done", re-check **only** the missing keys with `CheckTenantSecretExists`.
4. Do not continue until every required key returns `exists: true`.
5. If a plugin has `requiresAuthorization: true`, note it needs `--authorized` at run time (separate from vault).

## Next

When verify passes → load `rules-manager` (silently).

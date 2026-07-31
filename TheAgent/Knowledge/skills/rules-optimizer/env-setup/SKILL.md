---
name: env-setup
description: Check required vault secrets for the chosen plugins + platform. Never accept pasted secret values. Load after plugin-config.
---

# Environment variables

**Knowledge in scope:** `requiredEnvs` from each plugin’s `.xianix/agent-setup.json` (via `ListAvailablePlugins` for chosen plugins + platform).

Do this yourself — never ask whether secrets are needed.

Typical keys: GitHub → `GITHUB-TOKEN`; Azure DevOps → `AZURE-DEVOPS-TOKEN`; models → `ANTHROPIC-API-KEY`.

1. Call `CheckTenantSecretExists` for each required key.
2. If any `exists: false`, tell them to add those exact keys in Studio → Settings → Secrets, then say "done". Never ask them to paste values in chat.
3. On "done", re-check missing keys only.
4. If a plugin has `requiresAuthorization: true`, note it needs `--authorized` at run time (separate from vault).

## Next

When all required keys exist → load `rules-manager` (silently — never mention skills).

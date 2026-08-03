---
name: rules-manager
description: Show proposed rules.json changes, ask once for confirmation, then InstallPlugins. Load after env-setup.
---

# Rules.json update

**Knowledge in scope:** merge/save of activation rules. Prefer `InstallPlugins` (atomic).

1. Call `GetCurrentRules`.
2. Show a one-line proposal and ask **once**:

```
I'll save {plugins} to rules.json for {repo} ({platform}, inferred from the URL). Save now?
```

3. On confirm → `InstallPlugins` with the full desired short names + repo URL (platform is inferred from the URL; local recipes use the matching label/tag rules).
4. If `ok=true` and `claimAllowed=true`, briefly confirm **that result's** `installedShortNames`.
5. If `ok=false` / `claimAllowed=false`, say save failed and retry — never claim success.

`MaterializePluginRules` is preview-only (`notPersisted`). Never say "installed" from it.

## Next

On successful install → load `webhook-setup`. That skill must tell the user **How to trigger** (platform `suggestedTriggers`, e.g. GitHub label `ai-dlc/pr/pr-review` for pr-reviewer) **and** ask whether to create the Xians webhook — do not create until they say yes.

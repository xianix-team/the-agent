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

3. On confirm → `InstallPlugins` with the full desired short names + repo URL (platform is inferred from the URL; agent-setup executions use the matching label/tag rules).
4. If `ok=true` and `claimAllowed=true`, briefly confirm **that result's** `installedShortNames`.
5. If `ok=false` / `claimAllowed=false`, say save failed and retry — never claim success.

`MaterializePluginRules` is preview-only (`notPersisted`). Never say "installed" from it.

## Next

On successful install → **immediately** load `webhook-setup` (do not ask about webhooks; never mention skills).

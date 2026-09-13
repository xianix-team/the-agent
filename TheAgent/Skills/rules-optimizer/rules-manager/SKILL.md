---
name: rules-manager
description: Permission then InstallPlugins or SaveRules; never send the user to edit Studio Knowledge.
---

# Rules.json update

This skill teaches the **workflow**. Tools perform the writes.

Follow **context → action → evidence**. Prefer `InstallPlugins` for installs.
Use `GetCurrentRules` + `SaveRules(replaceExisting=true)` only for full-document rewrites —
and **always** pass `rulesJson`. Prefer `RemoveRulesEntries` for named execution / with-envs deletes.

**Do not write `rules.json` until the user explicitly agrees** after seeing the plan —
except when they already asked you to remove specific blocks (then confirm briefly and act).

**Forbidden:** “Go to Studio → Knowledge and delete these executions yourself.”
You apply every rules change with tools.

## Context

1. Call `GetTenantState` (silent) if you need a fresh snapshot.
2. Call `GetCurrentRules` if you need the raw document.
3. Call `ListAvailablePlugins` with the inferred platform if you need suggested triggers again.
4. Show a short plan and ask **once** for permission (install flow):

```
Ready to update rules.json for {repo} ({platform}):

Plugins: {plugins}

Update rules.json with this now?
```

## Action

### Install / replace plugin set

5. On confirm → `InstallPlugins` with the full desired short names.
   - Removing plugins from the set: pass the kept names with `replaceExistingSet=true`.

### Surgical delete (drop executions / with-envs)

5. Prefer `RemoveRulesEntries(executionNames=…, withEnvNames=…)`.
6. Re-read with `GetCurrentRules` before claiming the blocks are gone.

There is no `MaterializePluginRules`, `UpdateTriggerLabel`, `VerifyInstalledPlugins`,
`GetPluginSetupGuide`, or `skipExecutions` tool — do not call or invent them.

## Evidence

7. Success only if `ok=true` and `claimAllowed=true` (or `GetCurrentRules` proves the edit).
8. If save/install fails, say so and retry — never claim success.

```
5. Save rules.json: ✅ {short summary from tool fields}
```

## Next

On verified install → load `webhook-setup`.
On verified cleanup-only edit → stop with a clear completed line (unless they want webhook next).

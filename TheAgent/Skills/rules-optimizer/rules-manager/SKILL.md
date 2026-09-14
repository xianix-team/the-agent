---
name: rules-manager
description: Permission then InstallPlugins/SaveRules/RemoveRulesEntries; includes plugin cleanup. Never send user to edit Studio Knowledge.
---

# Rules.json (install + cleanup)

This skill teaches the **workflow**. Tools perform the writes.
Follow **context → action → evidence**. Prefer `InstallPlugins` for installs.
Use `GetCurrentRules` + `SaveRules(replaceExisting=true)` only for full-document rewrites —
and **always** pass `rulesJson`. Prefer `RemoveRulesEntries` for named execution / with-envs deletes.

**Do not write `rules.json` until the user explicitly agrees** after seeing the plan —
except when they already asked you to remove specific blocks (then confirm briefly and act).

**Forbidden:** “Go to Studio → Knowledge and delete these executions yourself.”
You apply every rules change with tools.

There is no `MaterializePluginRules`, `UpdateTriggerLabel`, `VerifyInstalledPlugins`,
`GetPluginSetupGuide`, or `skipExecutions` tool — do not call or invent them.

---

## Part A — Install / update plugin set

### Context

1. Call `GetTenantState` (silent) if you need a fresh snapshot.
2. Call `GetCurrentRules` if you need the raw document.
3. Call `ListAvailablePlugins` with the inferred platform if you need suggested triggers again.
4. Show a short plan and ask **once** for permission (install flow):

```
Ready to update rules.json for {repo} ({platform}):

Plugins: {plugins}

Update rules.json with this now?
```

### Action

5. On confirm → `InstallPlugins` with the full desired short names.
   - Removing plugins from the set: pass the kept names with `replaceExistingSet=true`.

### Evidence

6. Success only if `ok=true` and `claimAllowed=true` (or `GetCurrentRules` proves the edit).
7. If save/install fails, say so and retry — never claim success.

```
5. Save rules.json: ✅ {short summary from tool fields}
```

### Next (install path)

On verified install → load `webhook-setup`.

---

## Part B — Cleanup / uninstall / surgical delete

Use this part when the user wants to remove plugins, executions, or with-envs entries
(formerly a separate uninstall skill).

### Context

1. Call `GetTenantState` (and `GetCurrentRules` when you need raw JSON).
2. Confirm what to remove (plugin short names and/or named executions / with-envs entries).

### Action

#### B1 — Remove plugins from the install set

3. Build the **kept** short-name list.
4. Call `InstallPlugins` with:
   - `pluginNames` = remaining short names (comma-separated)
   - **`replaceExistingSet=true`** (required — without this, omitted plugins stay)
5. To clear **all** plugins: `InstallPlugins` with empty `pluginNames` and `replaceExistingSet=true`.

#### B2 — Remove specific executions or with-envs (keep plugins)

3. Call `RemoveRulesEntries` with:
   - `executionNames` = comma-separated execution `name` values to delete
   - and/or `withEnvNames` = comma-separated `with-envs` `name` values to delete
4. Do **not** call `SaveRules` without a full `rulesJson` — prefer `RemoveRulesEntries`.
5. Re-read with `GetCurrentRules` before claiming the blocks are gone.

### Evidence

6. Success only from tool results:
   - `InstallPlugins` / `RemoveRulesEntries` / `SaveRules` with `ok=true` and `claimAllowed=true`, and/or
   - a fresh `GetCurrentRules` showing the executions / with-envs / plugins are gone.
7. Report a short completed/failed line. Never claim success from chat intent alone.

### Next (cleanup path)

If they want to add another plugin → load `getting-started` (or jump to marketplace choice if intent is clear).
Otherwise stop with a clear completed line (unless they ask for webhook next → `webhook-setup`).

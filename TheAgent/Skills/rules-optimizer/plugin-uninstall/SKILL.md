---
name: plugin-uninstall
description: Remove plugins via InstallPlugins or executions/envs via RemoveRulesEntries — never ask the user to edit Studio Knowledge.
---

# Plugin / rules cleanup

This skill teaches the **workflow**. Tools perform the removals.

Follow **context → action → evidence**.

**Forbidden:** telling the user to open Studio → Knowledge and edit/delete rules by hand.
You must apply the change with tools.

## Context

1. Call `GetTenantState` (and `GetCurrentRules` when you need raw JSON).
2. Confirm what to remove (plugin short names and/or named executions / with-envs entries).

## Action

### A — Remove plugins from the install set

3. Build the **kept** short-name list.
4. Call `InstallPlugins` with:
   - `pluginNames` = remaining short names (comma-separated)
   - **`replaceExistingSet=true`** (required — without this, omitted plugins stay)
5. To clear **all** plugins: `InstallPlugins` with empty `pluginNames` and `replaceExistingSet=true`.

### B — Remove specific executions or with-envs (keep plugins)

3. Call `RemoveRulesEntries` with:
   - `executionNames` = comma-separated execution `name` values to delete
   - and/or `withEnvNames` = comma-separated `with-envs` `name` values to delete
4. Do **not** call `SaveRules` without a full `rulesJson` — prefer `RemoveRulesEntries`.

## Evidence

7. Success only from tool results:
   - `InstallPlugins` / `RemoveRulesEntries` / `SaveRules` with `ok=true` and `claimAllowed=true`, and/or
   - a fresh `GetCurrentRules` showing the executions / with-envs / plugins are gone.
8. Report a short completed/failed line. Never claim success from chat intent alone.

## Next

If they want to add another plugin → `plugin-marketplace`. Otherwise stop.

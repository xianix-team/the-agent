---
name: plugin-uninstall
description: Context installed set; action replaceExistingSet InstallPlugins; verify with VerifyInstalledPlugins.
---

# Plugin removal

Follow **context → action → verify**.

## Context

1. Call `GetCurrentRules` / `VerifyInstalledPlugins` — only remove plugins that are actually installed.
2. Confirm which short name(s) to remove.

## Action

3. Rebuild the **kept** set via `InstallPlugins` with:
   - `pluginNames` = remaining short names (comma-separated)
   - same repo URL (platform re-inferred)
   - **`replaceExistingSet=true`** (required — without this, omitted plugins stay installed)
4. To remove **all** plugins: `InstallPlugins` with empty `pluginNames` and `replaceExistingSet=true`.

## Verify

5. Call `VerifyInstalledPlugins` before claiming removal succeeded. Expected short names must match the kept set (or empty).

Do **not** rely on merge-only `SaveRules` for uninstall — merge keeps existing `use-plugins` / executions absent from the draft.

## Next

If they want to add another plugin → `plugin-marketplace`. Otherwise stop.

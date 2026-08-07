---
name: plugin-uninstall
description: Optional. Remove a plugin from activation rules.json. Load only when the user chooses to remove/modify by uninstall.
---

# Plugin removal

1. Call `GetCurrentRules` / `VerifyInstalledPlugins` — only remove plugins that are actually installed.
2. Confirm which short name(s) to remove.
3. Rebuild the **kept** set via `InstallPlugins` with:
   - `pluginNames` = remaining short names (comma-separated)
   - same repo URL (platform re-inferred)
   - **`replaceExistingSet=true`** (required — without this, omitted plugins stay installed)
4. To remove **all** plugins: `InstallPlugins` with empty `pluginNames` and `replaceExistingSet=true` (saves a fresh empty activation skeleton).
5. Confirm with `VerifyInstalledPlugins` before claiming removal succeeded.

Do **not** rely on merge-only `SaveRules` for uninstall — merge keeps existing `use-plugins` / executions absent from the draft.

## Next

If they want to add another plugin → `plugin-marketplace`. Otherwise stop.

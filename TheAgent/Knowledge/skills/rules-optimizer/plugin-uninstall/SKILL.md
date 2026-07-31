---
name: plugin-uninstall
description: Optional. Remove a plugin from activation rules.json. Load only when the user chooses to remove/modify by uninstall.
---

# Plugin removal

1. Call `GetCurrentRules` / `VerifyInstalledPlugins` — only remove plugins that are actually installed.
2. Confirm which short name(s) to remove.
3. Rebuild remaining plugins via `InstallPlugins` with the kept set + the same repo URL (platform re-inferred), **or** draft a document without the removed plugin and `ValidateRulesJson` + `SaveRules`.
4. If no plugins remain, save a valid empty activation skeleton — do not leave corrupt JSON.
5. Confirm with `VerifyInstalledPlugins` before claiming removal succeeded.

## Next

If they want to add another plugin → `plugin-marketplace`. Otherwise stop.

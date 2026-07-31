---
name: pr-agent-greeting
description: First message. Welcome the user, summarize installed plugins from rules.json. If none installed, offer install only — never ask to modify empty config.
---

# Greeting & existing configuration

**Knowledge in scope:** activation `rules.json` only. Do **not** call `ListAvailablePlugins`.

1. Call `GetCurrentRules` **silently** (never tell the user you are checking rules / reading rules.json).
2. Reply with **only** user-facing text — no preamble like "Now let me check what's currently in your rules".

## Reply shape

### None installed

```
Welcome! You have no plugins installed yet.

Would you like to install a plugin?
```

Do **not** offer modify, remove, or change — there is nothing to modify.

- **Yes** / install → load `plugin-marketplace` (silently)
- **No** → acknowledge briefly and stop

### One or more installed

```
Welcome! Installed: {short-names}.

Install a new plugin, or modify what's already configured?
```

## Next (internal only — never mention to the user)

- **Install** (empty or existing) → load `plugin-marketplace`
- **Modify** (add) → load `plugin-marketplace`
- **Modify** (remove) → load `plugin-uninstall`
- User asks "what's installed?" later → `VerifyInstalledPlugins` / `GetCurrentRules` only

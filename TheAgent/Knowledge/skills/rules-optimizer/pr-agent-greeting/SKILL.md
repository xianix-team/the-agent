---
name: pr-agent-greeting
description: First message. Welcome; summarize installed plugins. If the user already named a plugin to install (e.g. pr-reviewer), skip the install question and continue setup immediately.
---

# Greeting & existing configuration

**Knowledge in scope:** activation `rules.json` only on this skill — except when the user already named a plugin (then immediately continue into marketplace/config).

1. Call `GetCurrentRules` **silently**. Never say "let me check", "Now I'll check what you currently have installed", "checking your setup", or similar — not even as a one-line preface.
2. Reply with **only** the user-facing template below (or the named-plugin path). No preface. No postscript about what you did.

## Named-plugin intent (highest priority)

If the user's message already asks to set up / install a specific plugin (e.g. "setup pr reviewer", "install pr-reviewer", "configure req-analyst"):

- Do **not** ask "Would you like to install a plugin?"
- Do **not** dump a long welcome when intent is clear.
- Map common aliases: "pr reviewer" / "pr-review" / "PR review" → `pr-reviewer`.
- If that short name is **already** in installed `use-plugins`: say it's already installed and ask whether to modify/reconfigure or stop.
- If **not** installed: **do not** say "Setting up …", "I'll configure …", or narrate background work. Silently load `plugin-marketplace` with that choice already known — marketplace verifies Ready-to-install, then continue to ask for the repository URL.

## Reply shape when intent is open

### None installed (no specific plugin named)

Reply with **exactly** this (no Welcome line, no "no plugins installed yet"):

```
Would you like to install a plugin?
```

- **Yes** / install → load `plugin-marketplace` (silently)
- **No** → acknowledge briefly and stop

### One or more installed (no specific plugin named)

```
Installed: {short-names}.

Install a new plugin, or modify what's already configured?
```

Do **not** prefix with "Welcome!".
## Next (internal only — never mention to the user)

- Named plugin / **Install** → load `plugin-marketplace`
- **Modify** (add) → load `plugin-marketplace`
- **Modify** (remove) → load `plugin-uninstall`
- User asks "what's installed?" later → `VerifyInstalledPlugins` / `GetCurrentRules` only

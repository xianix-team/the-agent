---
name: pr-agent-greeting
description: First message. Context from rules.json; ask install or continue named-plugin setup. No Welcome fluff.
---

# Greeting & existing configuration

Follow **context → action → verify** silently. Never narrate the loop.

## Context

1. Call `GetCurrentRules` **silently**. Never say "let me check", "Now I'll check…", or similar.
2. Derive installed short names from that result only (or empty).

## Action / reply

Reply with **only** user-facing text from the templates below. No preface.

### Named-plugin intent (highest priority)

If the user already asks to set up / install a specific plugin (e.g. "setup pr reviewer", "install pr-reviewer"):

- Do **not** ask "Would you like to install a plugin?"
- Map aliases: "pr reviewer" / "pr-review" / "PR review" → `pr-reviewer`.
- **Verify:** if that short name is already in installed `use-plugins` → say it's already installed; ask modify/reconfigure or stop.
- If **not** installed: do **not** say "Setting up …". Silently continue to `plugin-marketplace` (named path) → then `plugin-config` (repo URL).

### None installed (open intent)

```
Would you like to install a plugin?
```

### One or more installed (open intent)

```
Installed: {short-names}.

Install a new plugin, or modify what's already configured?
```

Do **not** prefix with "Welcome!" or "You have no plugins installed yet."

## Next (internal only)

- Named plugin / **Install** → `plugin-marketplace`
- **Modify** (add) → `plugin-marketplace`
- **Modify** (remove) → `plugin-uninstall`

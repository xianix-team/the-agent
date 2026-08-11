---
name: pr-agent-greeting
description: First message. Welcome, show setup checklist, GetTenantState, ask install or continue named-plugin setup.
---

# Greeting & existing configuration

Follow **context → action → evidence** silently. Never narrate the loop.
Use the **Default** webhook / default prompt set for this run.

## Context

1. Call `GetTenantState` **silently**.
2. Optionally confirm with `GetCurrentRules` if you need raw content — never say "let me check".
3. Derive installed short names from tool results only (or empty).

## Action / reply

Open with welcome + checklist once, then the next question. No tool/process narration.

```
Welcome to Rules Optimizer!

We'll set this up in these steps:
1. Choose plugin(s)
2. Confirm repository
3. Configure match-any / triggers
4. Check secrets
5. Save rules.json
6. Create Xians webhook (Default)
7. Connect SCM (GitHub auto / Azure DevOps manual)
8. Setup completed or failed
```

### Named-plugin intent (highest priority)

If the user already asks to set up / install a specific plugin (e.g. "setup pr reviewer", "install pr-reviewer"):

- Map aliases: "pr reviewer" / "pr-review" / "PR review" → `pr-reviewer`.
- **Verify:** if that short name is already in installed `use-plugins`, after the checklist ask:

```
{plugin} is already installed. Would you like to modify it, or leave it as-is?
```

- If **not** installed: welcome + checklist once, then silently continue to `plugin-marketplace` → `plugin-config`. Do **not** say "Setting up …".

### None installed (open intent)

After checklist:

```
Would you like to install a plugin?
```

### One or more installed (open intent)

After checklist:

```
Installed: {short-names}.

Install a new plugin, or modify what's already configured?
```

Do **not** say "You have no plugins installed yet."

## Evidence

Mark nothing complete yet on greeting unless a named plugin is already installed (then note `1. Choose plugin(s): ✅ {plugin} (already installed)` only when verified from `GetTenantState`).

## Next (internal only)

- Named plugin / **Install** → `plugin-marketplace`
- **Modify** (add) → `plugin-marketplace`
- **Modify** (remove) → `plugin-uninstall`

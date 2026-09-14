---
name: getting-started
description: Welcome + checklist, GetTenantState, live marketplace; user chooses Ready plugin(s); then plugin-setup.
---

# Getting started (greeting + marketplace)

Follow **context → action → evidence** silently. Never narrate the loop.
Use the **Default** webhook / default prompt set for this run.

This skill covers greeting **and** marketplace discovery. Do **not** load separate greeting/marketplace skills.

## Part A — Greeting & existing configuration

### Context

1. Call `GetTenantState` **silently**.
2. Call `ListAvailablePlugins` with no platform filter. This is a fresh, no-cache marketplace + README availability check.
3. Optionally confirm with `GetCurrentRules` if you need raw content.
4. Derive installed and configurable short names from tool results only.

### Action / reply

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

You can start with:
- Install a plugin
- Show available plugins
- Show what is currently installed
- Create or reuse the Default webhook

Available now: {configurable plugin short names from ListAvailablePlugins}
```

#### Named-plugin intent (highest priority)

If the user already asks to set up / install a specific plugin (e.g. "setup pr reviewer", "install pr-reviewer"):

- Map aliases: "pr reviewer" / "pr-review" / "PR review" → `pr-reviewer`.
- **Verify:** if that short name is already in installed `use-plugins`, after the checklist ask:

```
{plugin} is already installed. Would you like to modify it, or leave it as-is?
```

- If **not** installed: welcome + checklist once, then continue to **Part B** in this skill (do not re-ask which plugin). Do **not** say "Setting up …".

#### None installed (open intent)

After the checklist, starting prompts, and live plugin list:

```
Would you like to install a plugin?
```

#### One or more installed (open intent)

After checklist:

```
Installed: {short-names}.

Install a new plugin, or modify what's already configured?
```

Do **not** say "You have no plugins installed yet."

### Evidence

Mark nothing complete yet on greeting unless a named plugin is already installed (then note `1. Choose plugin(s): ✅ {plugin} (already installed)` only when verified from `GetTenantState`).

### Branching (internal)

- **Modify** (remove / cleanup) → load `rules-manager` (cleanup section).
- **Install** / **Modify** (add) / named plugin not installed → continue **Part B** below.
- User only wants Default webhook / SCM → load `webhook-setup`.

---

## Part B — Marketplace discovery

This part teaches the **workflow**. `ListAvailablePlugins` performs the fetch.

Source: official `xianix-team/plugins-official` marketplace.json via `ListAvailablePlugins`.

Installability: live README (`plugins/<folder>/README.md`) on plugins-official.
Do **not** require local recipes or `.xianix/agent-setup.json`.

### Context

1. Call `GetTenantState` (silent) if not fresh this turn.
2. Call `ListAvailablePlugins` with **no** platform filter (unless already done in Part A this turn).
3. If `ok: false`, say marketplace unreachable and retry — do not invent a list.

### Action

#### Plugin already named

- Do **not** ask them to pick again. Do **not** paste the full list unless they ask.
- Do **not** say "Setting up …".

#### Plugin not chosen

- Show **Ready to install** (name + one-line description) and **Coming soon** (name only).
- Ask which Ready-to-install plugin(s) to use.

Do **not** ask for the repo URL or platform here.

### Evidence

- Named or chosen short name must appear in `readyToInstall` / `installable: true` before continuing.
- Evidence line: `1. Choose plugin(s): ✅ {short-names}` (only after marketplace confirms installable).
- If Coming soon / missing: say so and show Ready-to-install options — do not mark step complete.
- Use `ListAvailablePlugins` fields only for marketplace facts.

### Next

After verified choice → load `plugin-setup` (silent).

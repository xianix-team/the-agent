---
name: plugin-marketplace
description: GetTenantState + live marketplace; user chooses plugin (or accept named). Evidence Ready-to-install before config.
---

# Marketplace discovery

Follow **context → action → evidence**. Source: https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json

Installability: live README (`plugins/<folder>/README.md`) **plus** local execution recipe. Do **not** use `.xianix/agent-setup.json`.

## Context

1. Call `GetTenantState` (silent).
2. Call `ListAvailablePlugins` with **no** platform filter.
3. If `ok: false`, say marketplace unreachable and retry — do not invent a list.

## Action

### Plugin already named

- Do **not** ask them to pick again. Do **not** paste the full list unless they ask.
- Do **not** say "Setting up …".

### Plugin not chosen

- Show **Ready to install** (name + one-line description) and **Coming soon** (name only).
- Ask which Ready-to-install plugin(s) to use.

Do **not** ask for the repo URL or platform here. Never mention skills.

## Evidence

- Named or chosen short name must appear in `readyToInstall` / `installable: true` before continuing.
- Evidence line: `1. Choose plugin(s): ✅ {short-names}` (only after marketplace confirms installable).
- If Coming soon / missing: say so and show Ready-to-install options — do not mark step complete.

## Next

After verified choice → load `plugin-config` (silent).

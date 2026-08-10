---
name: plugin-marketplace
description: Context from live marketplace; user chooses plugin (or accept named). Verify Ready-to-install before config.
---

# Marketplace discovery

Follow **context → action → verify**. Source: https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json

Installability: live README (`plugins/<folder>/README.md`) **plus** local execution recipe. Do **not** use `.xianix/agent-setup.json`.

## Context

1. Call `ListAvailablePlugins` with **no** platform filter.
2. If `ok: false`, say marketplace unreachable and retry — do not invent a list.

## Action

### Plugin already named

- Do **not** ask them to pick again. Do **not** paste the full list unless they ask.
- Do **not** say "Setting up …".

### Plugin not chosen

- Show **Ready to install** (name + one-line description) and **Coming soon** (name only).
- Ask which Ready-to-install plugin(s) to use.

Do **not** ask for repo URL or platform here. Never mention skills.

## Verify

- Named or chosen short name must appear in `readyToInstall` / `installable: true` before continuing.
- If Coming soon / missing: say so and show Ready-to-install options.

## Next

After verified choice → load `plugin-config` (silent).

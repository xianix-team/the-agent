---
name: plugin-marketplace
description: List installable plugins from the official marketplace and let the user choose — or accept a plugin already named in chat. Do this BEFORE asking for the repo URL. No triggers here.
---

# Marketplace discovery

**Knowledge in scope:** official marketplace via `ListAvailablePlugins` only.

Source: https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json

Installability: live plugin README on plugins-official (`plugins/<folder>/README.md`, from marketplace `source`) **plus** a local execution recipe. Missing README or recipe → Coming soon. Do **not** look for `.xianix/agent-setup.json`.

Do **not** invent plugins or reuse a remembered list. If `ok: false`, say marketplace unreachable and retry.

1. Call `ListAvailablePlugins` with **no** platform filter (repo URL / platform not known yet).

## Plugin already chosen in chat

If the user (or greeting) already named a Ready-to-install short name (e.g. `pr-reviewer`):

- Confirm it is in `readyToInstall` / `installable: true`.
- Do **not** ask them to pick again. Do **not** paste the full marketplace list unless they ask.
- Do **not** say "Setting up …" or narrate verification. Go straight to the repository URL question (load `plugin-config`).

If that name is Coming soon / missing: say so and show Ready-to-install options so they can pick another.

## Plugin not chosen yet

2. Show briefly:
   - **Ready to install** (`installable: true`) — name + one-line description
   - **Coming soon** (`installable: false`) — name only
3. Ask which Ready-to-install plugin(s) to use. Only those may be chosen.

Do **not** ask for the repo URL or platform here. Do **not** show labels, tags, or triggers yet — those depend on the URL-inferred platform.

Never tell the user you are "loading a skill" or switching phases.

## Next

After choice (or confirmed named plugin) → load `plugin-config` (silent).

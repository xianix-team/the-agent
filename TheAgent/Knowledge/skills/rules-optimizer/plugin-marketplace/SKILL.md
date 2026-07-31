---
name: plugin-marketplace
description: List installable plugins from the official marketplace and let the user choose. Do this BEFORE asking for the repo URL. No triggers here.
---

# Marketplace discovery

**Knowledge in scope:** official marketplace via `ListAvailablePlugins` only.

Source: https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json

Installability: each plugin’s `plugins/<name>/.xianix/agent-setup.json` (fetched live). Missing/invalid setup → Coming soon.

Do **not** invent plugins or reuse a remembered list. If `ok: false`, say marketplace unreachable and retry.

1. Call `ListAvailablePlugins` with **no** platform filter (repo URL / platform not known yet).
2. Show briefly:
   - **Ready to install** (`installable: true`) — name + one-line description
   - **Coming soon** (`installable: false`) — name only
3. Ask which Ready-to-install plugin(s) to use. Only those may be chosen.

Do **not** ask for the repo URL or platform here. Do **not** show labels, tags, or triggers yet — those depend on the URL-inferred platform.

Never tell the user you are "loading a skill" or switching phases — after they choose, silently load `plugin-config` and ask the repo URL.

## Next

After choice → load `plugin-config` (silent).

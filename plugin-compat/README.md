# plugin-compat

Generic Xianix plugin compatibility layer.

Parses existing Claude/Xianix plugin trees into a harness-neutral `PluginDefinition`,
validates capabilities against a harness matrix, and adapts plugins to harness-native
runtime bundles.

## Phases

| Phase | Status | Scope |
|-------|--------|-------|
| 1 | Done | Parser, model, capability detection, compatibility validation |
| 2 | Done | `OpenCodePluginAdapter`, construct translation, executor wiring |
| 3 | Done | Second-plugin validation (`doc-writer`, `deadcode-scanner`) — no adapter changes |
| 4+ | Planned | Hooks/LSP, cache hardening, CI drift |

## Layout

```text
plugin-compat/
├── plugin_compat/
│   ├── parser.py
│   ├── model.py
│   ├── capabilities.py
│   ├── harness.py
│   ├── validator.py
│   ├── translate.py
│   ├── adapters/opencode.py    # OpenCodePluginAdapter (one for all plugins)
│   └── __main__.py             # CLI
├── scripts/
│   └── publish_to_open_executor.py
└── README.md
```

## Adapt a plugin for OpenCode

```bash
cd plugin-compat
python -m plugin_compat \
  --plugin-root ../../plugins-official/plugins/pr-reviewer \
  --output /tmp/generated-pr-reviewer

python -m plugin_compat \
  --plugin-root ../../plugins-official/plugins/doc-writer \
  --output /tmp/generated-doc-writer
```

## Publish into the OpenCode executor image

```bash
# from the-agent root — defaults include pr-reviewer + doc-writer
python plugin-compat/scripts/publish_to_open_executor.py --generate
```

This copies:

- `plugin_compat/` → `xianix-open-executor/vendor/plugin_compat/`
- official plugin snapshot(s) → `xianix-open-executor/vendor/plugins/<id>/`

Rebuild the executor image afterward.

## Runtime flow (OpenCode)

```text
vendor/plugins/<id>
        ↓
Plugin Parser → PluginDefinition
        ↓
Compatibility Validator
        ↓
OpenCodePluginAdapter → generated-plugins/<id>
        ↓
WORK_DIR/.opencode/{commands,agents,skills}
        ↓
opencode run  (slash command matched generically)
```

`CLAUDE_PLUGIN_ROOT` is set to the generated bundle root so existing scripts keep working.

## Design rules

- Do not branch on plugin names (`if plugin == "pr-reviewer"`).
- Existing official plugins remain unchanged.
- Unsupported capabilities are reported explicitly (never silently ignored).
- One OpenCode adapter for all plugins — not one adapter per plugin.

# Legacy monolithic catalogs

These files were the previous Rules Optimizer install recipes / setup requirements.
They are **not** loaded at runtime.

Use them only as input to regenerate per-plugin agent-setup files:

```bash
python tools/generate-agent-setup.py
```

Outputs:

- `tools/generated-agent-setup/plugins/<name>/.xianix/agent-setup.json` (plugins-official handoff)
- `TheAgent/Knowledge/agent-setup/<name>/agent-setup.json` (embedded offline fallback)

# Generated agent-setup.json (plugins-official handoff)

Copy each `plugins/<shortName>/.xianix/agent-setup.json` into
[xianix-team/plugins-official](https://github.com/xianix-team/plugins-official) at the same path.

Sync from test fixtures:

```bash
python tools/generate-agent-setup.py
```

Live fetch URL used by the-agent (runtime SoT — not these local files):

`https://raw.githubusercontent.com/xianix-team/plugins-official/main/plugins/<shortName>/.xianix/agent-setup.json`

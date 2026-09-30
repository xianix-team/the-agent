# Xianix Open Executor

OpenCode-only Docker image for Xianix. Same prepare / worktree / mise isolation as
`Executor/`, but the run phase always invokes OpenCode. Choose models via
`XIANIX-MODEL` (`provider/model`).

## Layout

| File | Purpose |
|------|---------|
| `Dockerfile` | Image: Node, git, gh, Azure CLI, OpenCode CLI, mise |
| `entrypoint.sh` | `XIANIX-MODE` → prepare / execute |
| `prepare_repo.sh` | Bare clone + worktree |
| `run_prompt.sh` | Runtimes + context + `execute_opencode.py` |
| `execute_opencode.py` | `opencode run` → JSON envelope |
| `host_context.py` | Platform / runtime / plugin recipe preamble |
| `adapter_runtime.py` | Generic plugin-compat adapt + slash-command match |
| `generate_context.sh` | Deterministic orientation + symbol map |
| `provision_runtimes.sh` | mise from **repo** version files only |
| `maintain_volume.sh` | git gc, sessions, mise prune |

`vendor/` is **not committed**. Produce it before image build:

```bash
python ../plugin-compat/scripts/publish_to_open_executor.py
```

## Build

```bash
# from the-agent/
python plugin-compat/scripts/publish_to_open_executor.py
cd xianix-open-executor/
docker build -t xianix-open-executor:latest .
```

```bash
# TheAgent/.env
EXECUTOR-IMAGE=xianix-open-executor:latest
```

## Models

```json
"model": "openai/gpt-5.3-codex"
```

Use an id from `opencode models` in the image. Inject matching API keys via `with-envs`
(e.g. `secrets.OPENAI-API-KEY`).

## Plugin compatibility

Claude marketplace install does **not** run here. Official plugins are adapted at
runtime by `plugin-compat` (`../plugin-compat/`):

```text
vendor/plugins/<id>
        ↓
Plugin Parser → PluginDefinition
        ↓
OpenCodePluginAdapter → generated-plugins/<id>
        ↓
WORK_DIR/.opencode/{commands,agents,skills}
        ↓
opencode run
```

Keep `use-plugins` in `rules.json` as usual. Switch harness via `EXECUTOR-IMAGE`.

## Not included

Claude Code CLI, Playwright, Claude marketplace install, hand-maintained per-plugin
recipe ports. This image runs OpenCode + the generic compatibility layer only.

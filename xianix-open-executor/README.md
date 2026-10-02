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
| `host_context.py` | Platform / runtime preamble |
| `generate_context.sh` | Deterministic orientation + symbol map |
| `provision_runtimes.sh` | mise from **repo** version files only |
| `maintain_volume.sh` | git gc, sessions, mise prune |

## Build

```bash
cd xianix-open-executor/
docker build -t xianix-open-executor:latest .
```

```bash
# TheAgent/.env
EXECUTOR-IMAGE=xianix-open-executor:latest
```

Switch harness with `EXECUTOR-IMAGE` only. **Rules are not interchangeable** between
images without edits:

| Claude executor (`xianix-executor`) | Open executor (`xianix-open-executor`) |
|-------------------------------------|----------------------------------------|
| `claude-sonnet-*` model ids | `provider/model` (e.g. `openai/gpt-5.3-codex`) |
| `use-plugins` + `/slash` in prompt | Full task in `execute-prompt`; `use-plugins` ignored |
| `secrets.ANTHROPIC-API-KEY` | `secrets.OPENAI-API-KEY` (or anthropic provider/model) |

The system seed `TheAgent/Knowledge/rules.json` in this branch is tuned for OpenCode.
For Claude executor, restore plugin blocks and Claude model ids (or maintain two
agent templates / activations with separate rule sets).

## Models

```json
"model": "openai/gpt-5.3-codex"
```

Use an id from `opencode models` in the image. Inject matching API keys via `with-envs`
(e.g. `secrets.OPENAI-API-KEY`).

OpenCode rejects bare Claude-style ids (`claude-sonnet-4-5`); they must be
`provider/model`.

## Plugins

This image does **not** install Claude marketplace plugins and does **not** run
plugin-compat adaptation. `use-plugins` / `CLAUDE-CODE-PLUGINS` from the agent are
ignored. Put the full task in `execute-prompt` (the model can still use `gh`, git,
etc. via OpenCode tools).

## Not included

Claude Code CLI, Playwright, Claude marketplace install, plugin-compat adapter.

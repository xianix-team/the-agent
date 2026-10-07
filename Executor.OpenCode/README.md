# Executor.OpenCode

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
| `plugin_runtime.py` | Convert Claude plugin `.md` → OpenCode overlay + slash-command match |
| `scripts/publish_plugins.py` | Copy official plugins into `vendor/plugins/` |
| `generate_context.sh` | Deterministic orientation + symbol map |
| `provision_runtimes.sh` | mise from **repo** version files only |
| `maintain_volume.sh` | git gc, sessions, mise prune |

`vendor/` is **not committed**. Produce it before image build:

```bash
python Executor.OpenCode/scripts/publish_plugins.py
```

## Tests

```bash
# from Executor.OpenCode/
python -m unittest discover -s tests -v
bash tests/test_common_security.sh

# full image suite (builds executor.opencode:integration-test):
bash tests/integration_test.sh
# SKIP_BUILD=1 IMAGE=executor.opencode:latest ./tests/integration_test.sh
```

## Build

```bash
# from the-agent/
python Executor.OpenCode/scripts/publish_plugins.py
cd Executor.OpenCode/
docker build -t executor.opencode:latest .
```

```bash
# TheAgent/.env
EXECUTOR-IMAGE=executor.opencode:latest
```

## Models

```json
"model": "openai/gpt-5.3-codex"
```

Use an id from `opencode models` in the image. Inject matching API keys via `with-envs`
(e.g. `secrets.OPENAI-API-KEY`).

## Plugins

Claude marketplace install does **not** run here. Official plugins keep Claude
markdown as the source (`commands/` / `agents/` / `skills/`) plus shared
`scripts/` and `providers/`. At run time the executor converts those `.md` files
into OpenCode frontmatter/body and writes only the converted markdown under
`WORK_DIR/.opencode/` — no fat bundle and no runtime marketplace install:

```text
vendor/plugins/<id>/{commands,agents,skills}   (Claude SoT)
        ↓ convert .md only
WORK_DIR/.opencode/{commands,agents,skills}
        ↓
opencode run
```

`CLAUDE_PLUGIN_ROOT` points at `vendor/plugins/<id>` so shared scripts keep working.
Keep `use-plugins` in `rules.json` as usual. Prefer per-execution
`"executor-image": "executor.opencode:latest"` in `rules.json` (no agent restart).
Host `EXECUTOR-IMAGE` remains the default when that field is omitted.

## Not included

Claude Code CLI, Playwright, Claude marketplace install. Committed `.opencode/`
trees in the plugin repo are optional leftovers; runtime prefers Claude markdown
conversion.

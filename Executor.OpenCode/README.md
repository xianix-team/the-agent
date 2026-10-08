# Executor.OpenCode

OpenCode-only Docker image for Xianix. Same prepare / worktree / mise isolation as
`Executor/`, but the run phase always invokes OpenCode. Choose models via
`XIANIX-MODEL` (`provider/model`).

## Layout

| File | Purpose |
|------|---------|
| `Dockerfile` | Image: Node, git, gh, Azure CLI, OpenCode CLI, Claude Code CLI (plugins only), mise |
| `entrypoint.sh` | `XIANIX-MODE` → prepare / execute |
| `prepare_repo.sh` | Bare clone + worktree |
| `run_prompt.sh` | Claude plugin install + OpenCode convert + runtimes + `execute_opencode.py` |
| `execute_opencode.py` | `opencode run` → JSON envelope |
| `host_context.py` | Platform / runtime / plugin recipe preamble |
| `plugin_runtime.py` | Convert Claude plugin `.md` → OpenCode overlay + slash-command match |
| `generate_context.sh` | Deterministic orientation + symbol map |
| `provision_runtimes.sh` | mise from **repo** version files only |
| `maintain_volume.sh` | git gc, sessions, mise prune |

## Tests

```bash
# from Executor.OpenCode/
python -m unittest discover -s tests -v

# full image suite (builds xianix-executor-opencode:integration-test):
bash tests/integration_test.sh
# SKIP_BUILD=1 IMAGE=xianix-executor-opencode:latest ./tests/integration_test.sh
```

## Build

```bash
# from Executor.OpenCode/
docker build -t xianix-executor-opencode:latest .
```

```bash
# TheAgent/.env (host default; per-execution override via rules.json)
EXECUTOR-IMAGE=xianix-executor:latest
```

```json
"executor-image": "xianix-executor-opencode:latest"
```

## Models

```json
"model": "openai/gpt-5.3-codex"
```

Use an id from `opencode models` in the image. Inject matching API keys via `with-envs`
(e.g. `secrets.OPENAI-API-KEY`).

## Plugins

Same `CLAUDE-CODE-PLUGINS` / `use-plugins` contract as the Claude executor. Each run:

1. `claude plugin marketplace remove` (stale) + `marketplace add` (fresh) — same as `Executor/`
2. `claude plugin install <plugin>@<marketplace> --scope project`
3. Stage each `installPath` into `XIANIX_PLUGINS_DIR/<name>/`
4. Convert Claude `commands/` / `agents/` / `skills/` `.md` into `WORK_DIR/.opencode/`
5. `opencode run` (not Claude Code for the prompt)

```text
claude plugin marketplace add / plugin install
        ↓ stage installPath
XIANIX_PLUGINS_DIR/<id>/{commands,agents,skills}
        ↓ convert .md only
WORK_DIR/.opencode/{commands,agents,skills}
        ↓
opencode run
```

`CLAUDE_CONFIG_DIR` (on the repo volume) holds marketplace clones and the plugin
cache, matching Claude executor behaviour. `CLAUDE_PLUGIN_ROOT` points at the
staged plugin so shared scripts keep working.

## Not included

Playwright / browsers. Claude Code is present only for plugin marketplace
install — the agent prompt always runs through OpenCode.

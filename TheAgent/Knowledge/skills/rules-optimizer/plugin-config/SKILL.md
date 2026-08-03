---
name: plugin-config
description: After plugins are chosen, ask repo URL; infer platform; show execution options (label + match combinations) and get user acceptance before secrets/save. Load after plugin-marketplace.
---

# Plugin configuration

**Order:** plugins are already chosen. Ask for the repository URL — then infer the platform from that URL. Do **not** ask “GitHub or Azure DevOps?”.

Supported hosts: **github.com** → `github`; **dev.azure.com** / **\*.visualstudio.com** → `azuredevops`. No GitLab.

Ask only (after a brief ack of their plugin choice if needed — never mention skills):

```
What is the repository URL? (e.g. https://github.com/org/repo.git or https://dev.azure.com/org/project/_git/repo)
```

## Infer platform from URL (mandatory)

| URL host | Platform |
|----------|----------|
| `github.com` / `www.github.com` | `github` |
| `dev.azure.com` / `*.visualstudio.com` | `azuredevops` |

If the host is unknown, say you only support github.com and Azure DevOps cloud URLs — do not guess.

Briefly confirm: `Got it — GitHub repo.` / `Got it — Azure DevOps repo.`

## Show execution options — wait for acceptance (mandatory)

**Before** env-setup or writing `rules.json`, call `ListAvailablePlugins` **with the inferred platform**. Confirm each chosen plugin is Ready to install.

From each chosen plugin’s **`executionOptions`** (and `suggestedTriggers`), show the user what will be configured — do **not** dump raw JSON.

### GitHub example (pr-reviewer)

```
Execution options for pr-reviewer on GitHub:

1. github-pull-request-review
   - Default label: `ai-dlc/pr/pr-review`
   - Matches when (any of):
     - Label `ai-dlc/pr/pr-review` applied to an open PR
     - PR opened already carrying that label
     - New commits on an open PR with that label

2. github-pr-agent-comment-instruction
   - Matches when: PR comment mentioning `@xianix`

Accept these defaults, or tell me a different trigger label (e.g. `pr-review-agent`)?
```

### Azure DevOps

Use `executionOptions` / `suggestedTriggers` for ADO wording (PR created / source branch updated / reviewer / `@xianix` comment) — **not** GitHub label names.

Rules:

- Wait for the user to accept defaults **or** choose a custom label / clarify options.
- Never invent labels/tags or mix platforms.
- Do **not** update `rules.json` in this skill.
- Never store a concrete URL with `constant: false`. Do not add `repository.ref`.

## Next

Only after the user accepts (or customizes) execution options → load `env-setup` (silently).

---
name: plugin-config
description: Context from URL + marketplace; show execution options; verify user acceptance before secrets/save.
---

# Plugin configuration

Follow **context → action → verify**. Plugins are already chosen. Do **not** ask “GitHub or Azure DevOps?”.

## Context

Ask for the repository URL:

```
What is the repository URL? (e.g. https://github.com/org/repo.git or https://dev.azure.com/org/project/_git/repo)
```

Infer platform from host only:

| URL host | Platform |
|----------|----------|
| `github.com` / `www.github.com` | `github` |
| `dev.azure.com` / `*.visualstudio.com` | `azuredevops` |

Unknown host → say only github.com and Azure DevOps cloud are supported — do not guess.

Briefly confirm: `Got it — GitHub repo.` / `Got it — Azure DevOps repo.`

Then call `ListAvailablePlugins` **with the inferred platform**. Confirm each chosen plugin is Ready to install.

## Action

Show **`executionOptions`** / `suggestedTriggers` for the inferred platform — not raw JSON. Wait for accept or custom label.

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

Use ADO wording from `executionOptions` / `suggestedTriggers` — **not** GitHub label names.

## Verify

- User accepted defaults **or** chose a custom label / clarified options.
- Never invent labels/tags or mix platforms.
- Do **not** update `rules.json` in this skill.
- Never store a concrete URL with `constant: false`. Do not add `repository.ref`.

## Next

Only after verified acceptance → load `env-setup` (silently).

---
name: plugin-config
description: GetTenantState then confirm repo from list (or ask URL); show executions and match-any; verify before secrets/save.
---

# Plugin configuration

Follow **context → action → evidence**. Plugins are already chosen. Do **not** ask “GitHub or Azure DevOps?”.

## Context

1. Call `GetTenantState` **silently**.
2. Repository selection — use `repositories.distinct` (deduped configured + onboarded; **`…/repo` and `…/repo.git` are the same repo**):

   - **0 distinct** → ask once for a clone URL:

```
What is the repository URL? (e.g. https://github.com/org/repo.git or https://dev.azure.com/org/project/_git/repo)
```

   - **1 distinct** → confirm that single URL (do not show a numbered multi-choice list):

```
I found this repository: {url}

Use this one? (yes, or paste a different clone URL)
```

   - **2+ distinct** → list each distinct URL once and ask which:

```
I already see these repositories:

1. {url-1}
2. {url-2}

Which one should we use? (reply with a number, or paste a new clone URL)
```

Never list the same repo twice (including with/without `.git`). Never ask a blank clone-URL question when at least one distinct repo is known.
Infer platform from host only:

| URL host | Platform |
|----------|----------|
| `github.com` / `www.github.com` | `github` |
| `dev.azure.com` / `*.visualstudio.com` | `azuredevops` |

Unknown host → say only github.com and Azure DevOps cloud are supported — do not guess.

Briefly confirm: `Got it — GitHub repo.` / `Got it — Azure DevOps repo.`

Then call `ListAvailablePlugins` **with the inferred platform**. Confirm each chosen plugin is Ready to install.

## Action — executions + match-any (mandatory)

For each chosen plugin, use that platform’s **`executionOptions`** (not raw JSON).

For **every** execution:

1. Show the execution name.
2. Show its **`match-any`** section clearly (OR alternatives — the run fires if **any** of these match).
   Use each entry’s `name` + `summary` from `executionOptions.matchAny` (or `suggestedTriggers` wording on ADO).
3. **Ask how they want to set `match-any` up** — do not assume defaults. Offer concrete choices, e.g.:
   - Keep all listed `match-any` alternatives
   - Keep only some (they name which)
   - Change the label / trigger value used in those rules (e.g. `pr-review-agent` instead of the default)
   - Drop this whole execution

Explain briefly in user language: *“These are the conditions that start this run. If any one matches, the agent runs.”*

### GitHub example (pr-reviewer)

```
Here are the executions for pr-reviewer on GitHub.

### github-pull-request-review
match-any (runs if any of these match):
1. Label applied — Label `ai-dlc/pr/pr-review` applied to an open PR
2. PR opened with label — PR opened already carrying label `ai-dlc/pr/pr-review`
3. Commits on labeled PR — New commits pushed to an open PR with label `ai-dlc/pr/pr-review`

How do you want to set this up?
- Keep all three
- Keep only some (tell me which)
- Use a different label for these matches (tell me the label)
- Skip this execution

### github-pr-agent-comment-instruction
match-any (runs if any of these match):
1. @xianix comment — PR comment mentioning `@xianix`

Keep this as-is, change it, or skip it?
```

### Azure DevOps

Same pattern: list each execution’s `match-any` / `suggestedTriggers` in ADO wording — **not** GitHub label names — then ask how to set each up.

## Evidence / Verify

Before leaving this skill, **restate the agreed setup** and get a clear yes:

```
2. Confirm repository: ✅ {url}
3. Configure match-any / triggers:

Confirming match-any for pr-reviewer:

github-pull-request-review — keep all three, label `pr-review-agent`
github-pr-agent-comment-instruction — keep @xianix comment

Does that look right?
```

Only after they confirm:

- Never invent labels/tags or mix platforms.
- Do **not** update `rules.json` in this skill.
- Never store a concrete URL with `constant: false`. Do not add `repository.ref`.

## Next

Only after verified match-any acceptance → load `env-setup` (silently).

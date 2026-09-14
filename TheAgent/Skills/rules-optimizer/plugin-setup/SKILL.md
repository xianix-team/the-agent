---
name: plugin-setup
description: Confirm repo + match-any; auto-check vault secrets; then rules-manager. Never ask if secrets exist.
---

# Plugin setup (config + secrets)

Follow **context → action → evidence**. Plugins are already chosen.
This skill covers repository / match-any **and** secrets. Do **not** load separate config/env skills.
Do **not** ask “GitHub or Azure DevOps?”. Do **not** save `rules.json` here.

## Part A — Repository + match-any

### Context

1. Call `GetTenantState` **silently**.
2. Repository selection — use `repositories.distinct` (deduped configured + onboarded; **`…/repo` and `…/repo.git` are the same repo**):

   - **0 distinct** → ask once for a repository URL (`.git` optional on GitHub):

```
What is the repository URL? (e.g. https://github.com/org/repo or https://github.com/org/repo.git, or https://dev.azure.com/org/project/_git/repo)
```

Accept browser and clone forms as the same repo — never ask the user to add or remove `.git`.

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

### Action — executions + match-any (mandatory)

For each chosen plugin, use that plugin’s live README on plugins-official (and the
marketplace description) for execution names and typical triggers on the inferred
platform. Do not invent labels from memory. `ListAvailablePlugins` does not return
recipe fields — read the README yourself when you need trigger wording.

**Webhook executions only.** Do **not** list `chat` or slash-command as an execution.

For **every webhook execution** you discuss:

1. Show the execution name.
2. Show match-any / trigger alternatives clearly (OR — any match runs the agent).
3. **Ask how they want to set it up** — keep all / keep some / different label / skip.

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

Same pattern in ADO wording — **not** GitHub label names.

### Evidence / Verify (Part A)

Before secrets, **restate the agreed setup** and get a clear yes:

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
- Do **not** update `rules.json` in this skill yet — that is `rules-manager` + tools.
- After confirm, `rules-manager` should `InstallPlugins` then progressively
  `SaveRules` for the agreed executions only (commons are seeded by tools).
- Never store a concrete URL with `constant: false`. Do not add `repository.ref`.
- Never invent executions the user did not confirm.
- Never leave rule-set `with-envs` empty when plugins/executions are present.

---

## Part B — Environment / secrets

Tools: `CheckTenantSecretExists` / `GetTenantState`. Never accept pasted secret values.

**You check secrets yourself.** Call tools — never ask the user whether a secret exists.

**Forbidden** (never say these):
- "Do you have GITHUB-TOKEN set up?"
- "Do you have this set up in Studio → Settings → Secrets?"
- "Is ANTHROPIC-API-KEY configured?"
- Any yes/no question about whether a vault key exists
- "What environment variables do you want to add?"
- Any open-ended menu of optional env vars / PR criteria / custom config for
  `with-envs` — keep seed defaults; only report missing vault keys

**Context source:** platform from the repo URL (GitHub → `GITHUB-TOKEN`; Azure DevOps →
`AZURE-DEVOPS-TOKEN`), plus always `ANTHROPIC-API-KEY`, plus `GetTenantState.secrets`.

Typical keys (always auto-check these when required by platform/plugins):
- GitHub → `GITHUB-TOKEN`
- Azure DevOps → `AZURE-DEVOPS-TOKEN`
- Models → **`ANTHROPIC-API-KEY`** (always required for plugin runs)

**Always include `ANTHROPIC-API-KEY` in the silent check.** Call
`CheckTenantSecretExists("ANTHROPIC-API-KEY")` (or use `GetTenantState.secrets`).
Only if `exists: false`, tell them to add it.

### Context / Action

1. Call `GetTenantState` (silent) — use `secrets[].exists` when present.
2. Call `CheckTenantSecretExists` for **every** required key (confirm live; do not trust chat memory).
3. If all `exists: true` → evidence line and continue — **do not mention secrets** to the user.
4. If any `exists: false` → state the fact only (no question):

```
4. Check secrets: ❌ missing {KEY}

{KEY} is missing. Add it in Studio → Settings → Secrets (exact key name), then say "done".
```

### Evidence

5. On "done", re-check **only** the missing keys with `CheckTenantSecretExists`.
6. Do not continue until every required key returns `exists: true`.
7. When all present: `4. Check secrets: ✅ {keys}` (optional one short line — no interrogation).

### Next

When Part A and Part B evidence pass → load `rules-manager` (silently).

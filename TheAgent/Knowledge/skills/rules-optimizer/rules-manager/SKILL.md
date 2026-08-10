---
name: rules-manager
description: Context plan; action InstallPlugins after permission; verify before confirming save.
---

# Rules.json update

Follow **context → action → verify**. Prefer `InstallPlugins` (atomic).

**Do not write `rules.json` until the user explicitly agrees** after seeing the execution plan.

## Context

1. Call `GetCurrentRules` (silent).
2. Call `ListAvailablePlugins` with the inferred platform if you need `executionOptions` / `suggestedTriggers` again.
3. Optionally `MaterializePluginRules` for a preview (`notPersisted`) — never say "installed" from it.
4. Show a short plan and ask **once** for permission:

```
Ready to update rules.json for {repo} ({platform}):

Plugins: {plugins}

Executions (pr-reviewer):
- github-pull-request-review — label `{label}` on open / labeled / synchronized PRs
- github-pr-agent-comment-instruction — `@xianix` on a PR comment

Update rules.json with this now?
```

Adapt bullets from the user’s accepted options / custom label — do not invent.

## Action

5. On confirm → `InstallPlugins` with the full desired short names + repo URL.
   - Custom label: after a successful install apply that label in execution `match-any` rules, then `ValidateRulesJson` + `SaveRules` — silently.

## Verify

6. Success only if `ok=true` and `claimAllowed=true` (or a fresh `VerifyInstalledPlugins` shows the expected short names). Confirm **that result's** `installedShortNames`.
7. If `ok=false` / `claimAllowed=false`, say save failed and retry — never claim success.

### Custom trigger label (later modify)

1. Context: current rules. 2. Action: update filters silently. 3. Verify: `ValidateRulesJson` + successful `SaveRules` / read-back. Reply only with outcome + how to trigger (+ webhook ask if pending).

```
✓ Trigger label updated to pr-review-agent.

How to trigger: Add the label `pr-review-agent` to a pull request.

May I create the Xians webhook so this works?
```

## Next

On verified install → load `webhook-setup`.

---
name: rules-manager
description: After secrets are ready, summarize chosen execution options and ask permission to update rules.json, then InstallPlugins. Load after env-setup.
---

# Rules.json update

**Knowledge in scope:** merge/save of activation rules. Prefer `InstallPlugins` (atomic).

**Do not write `rules.json` until the user explicitly agrees** after seeing the execution plan.

1. Call `GetCurrentRules` (silent).
2. Call `ListAvailablePlugins` with the inferred platform if you need `executionOptions` / `suggestedTriggers` again.
3. Optionally call `MaterializePluginRules` for a preview (`notPersisted`) — never say "installed" from it.
4. Show a short plan and ask **once** for permission to update `rules.json`. Include the execution options the user already accepted (label + match combinations):

```
Ready to update rules.json for {repo} ({platform}):

Plugins: {plugins}

Executions (pr-reviewer):
- github-pull-request-review — label `{label}` on open / labeled / synchronized PRs
- github-pr-agent-comment-instruction — `@xianix` on a PR comment

Update rules.json with this now?
```

Adapt the bullets from the user’s accepted options / custom label — do not invent.

5. On confirm → `InstallPlugins` with the full desired short names + repo URL.
   - If they chose a **custom label**, after a successful install apply that label in execution `match-any` rules, then `ValidateRulesJson` + `SaveRules` — silently (no “Updating the label from …” narration).
6. If `ok=true` and `claimAllowed=true`, briefly confirm **that result's** `installedShortNames`.
7. If `ok=false` / `claimAllowed=false`, say save failed and retry — never claim success.

## Custom trigger label (later modify)

When the user asks to change the label after rules are already saved:

1. Update filters silently.
2. Reply only with outcome + how to trigger (+ webhook ask if still pending).

```
✓ Trigger label updated to pr-review-agent.

How to trigger: Add the label `pr-review-agent` to a pull request.

May I create the Xians webhook so this works?
```

## Next

On successful install → load `webhook-setup` (how to trigger + ask to create webhook).

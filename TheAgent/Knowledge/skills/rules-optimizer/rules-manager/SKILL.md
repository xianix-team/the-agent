---
name: rules-manager
description: Context plan; InstallPlugins after permission; VerifyInstalledPlugins before confirming.
---

# Rules.json update

Follow **context → action → evidence**. Prefer `InstallPlugins` (atomic).

**Do not write `rules.json` until the user explicitly agrees** after seeing the execution plan.

## Context

1. Call `GetTenantState` (silent) if you need a fresh snapshot.
2. Call `GetCurrentRules` if you need the raw document.
3. Call `ListAvailablePlugins` with the inferred platform if you need `executionOptions` / `suggestedTriggers` again.
4. Optionally `MaterializePluginRules` for a preview (`notPersisted`) — never say "installed" from it.
5. Show a short plan and ask **once** for permission. Include the **verified match-any** the user already agreed in plugin-config:

```
Ready to update rules.json for {repo} ({platform}):

Plugins: {plugins}

Executions (pr-reviewer):
- github-pull-request-review
  match-any: label `pr-review-agent` on open / labeled / synchronized PRs (all three)
- github-pr-agent-comment-instruction
  match-any: `@xianix` on a PR comment

Update rules.json with this now?
```

Adapt the bullets from the user’s verified match-any choices / custom label — do not invent.

## Action

6. On confirm → `InstallPlugins` with the full desired short names + repo URL.
   - Custom label at install time: pass `triggerLabel` to `InstallPlugins` (do not hand-edit JSON).
   - After install, to change the label: call `UpdateTriggerLabel` — never invent a save.

## Evidence

7. Success only if `ok=true` and `claimAllowed=true` (or a fresh `VerifyInstalledPlugins` shows the expected short names). Confirm **that result's** `installedShortNames`.
8. If install fails, say so and retry — never claim success.

Evidence line example:

```
5. Save rules.json: ✅ installed {short-names}
```

### Custom trigger label (later modify)

1. Context: `GetTenantState` / `GetCurrentRules` (silent).
2. Action: **`UpdateTriggerLabel(newLabel=…)`** — required; do not hand-edit or claim success without it.
3. Evidence: tool `ok=true` + `claimAllowed=true` and `triggerLabel` matches. Reply only with outcome + how to trigger (+ webhook ask if pending).

```
✓ Trigger label updated to pr-review-agent.

How to trigger: Add the label `pr-review-agent` to a pull request.

May I create the Xians webhook so this works?
```

## Next

On verified install → load `webhook-setup`.

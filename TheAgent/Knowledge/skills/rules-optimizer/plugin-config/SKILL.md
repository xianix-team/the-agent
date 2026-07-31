---
name: plugin-config
description: After plugins are chosen, ask repo URL only; infer GitHub vs Azure DevOps from the URL host. Resolve platform-specific triggers. Load after plugin-marketplace.
---

# Plugin configuration

**Order:** plugins are already chosen. Ask for the repository URL — then infer the platform from that URL. Do **not** ask “GitHub or Azure DevOps?”.

Supported hosts: **github.com** → `github`; **dev.azure.com** / **\*.visualstudio.com** → `azuredevops`. No GitLab.

Ask only (after a brief ack of their plugin choice if needed — never mention skills):

```
What is the repository URL? (e.g. https://github.com/org/repo.git or https://dev.azure.com/org/project/_git/repo)
```

Bad (never say): "Now let me load the next skill to ask for the repository URL"
## Infer platform from URL (mandatory)

From the host:

| URL host | Platform |
|----------|----------|
| `github.com` / `www.github.com` | `github` |
| `dev.azure.com` / `*.visualstudio.com` | `azuredevops` |

If the host is unknown (self-hosted), say you only support github.com and Azure DevOps cloud URLs, and ask for a supported URL — do not guess.

Briefly confirm what you inferred, e.g. `Got it — GitHub repo.` / `Got it — Azure DevOps repo.`

Store URL as:

```json
"repository": { "url": { "value": "<url>", "constant": true } }
```

## Platform → triggers (mandatory)

After platform is inferred, call `ListAvailablePlugins` **with that platform**. Confirm each chosen plugin is Ready to install / in `supportedPlatforms`. If not, ask for a different Ready-to-install plugin.

From the tool result, take **`suggestedTriggers` for the inferred platform only**. Never invent labels/tags. Never mix GitHub and Azure DevOps triggers.

- **GitHub** → PR/Issue **labels** (e.g. `ai-dlc/pr/pr-review`)
- **Azure DevOps** → PR/work item **events** from `suggestedTriggers` (often created / updated / reviewer — not GitHub label names)

In one short line, tell the user how the selected plugin(s) will be triggered on this platform (from `suggestedTriggers`). Do not ask them to invent a label/tag.

Rules:

- Never store a concrete URL with `constant: false`.
- Do not add `repository.ref`.
- Pass the **inferred** platform into later `InstallPlugins` (tools also infer from the URL if platform is omitted).

## Next

When URL is valid and platform is inferred → load `env-setup` (silently — never mention skills to the user).

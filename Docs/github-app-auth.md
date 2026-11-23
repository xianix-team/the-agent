# GitHub App authentication

A tenant can authenticate to GitHub as a **GitHub App** instead of with a user's personal
access token (PAT). The agent then mints a short-lived **installation access token** for every
executor run, scoped to the one repository the run targets.

The App credentials go into the existing `GITHUB-TOKEN` secret, and the minted token is injected
into the container as `GITHUB-TOKEN`, exactly where the PAT used to go. `rules.json` and the
executor scripts are unchanged; git (`x-access-token` credential), the `gh` CLI (`GH_TOKEN`) and
plugin `curl` calls keep working. Plugins must not depend on `GET /user` (see Limitations); the
`pr-reviewer` plugin's permission check handles App tokens from plugins-official
`feature/github-app-auth` onwards.

> **GitHub only — not Azure DevOps.** This applies only to repositories on `github.com`. Azure DevOps
> has no GitHub App equivalent in the agent:
>
> - Azure DevOps runs keep using the `AZURE-DEVOPS-TOKEN` PAT, so their comments, votes and pushes
>   still appear as the person who owns that PAT. To show a non-person name there today, create a
>   dedicated service account in Azure DevOps and use its PAT.
> - Putting `<app-id>,<private-key>` in `GITHUB-TOKEN` does not change Azure DevOps runs. If such a
>   run also receives `GITHUB-TOKEN` (for example from a rule-set-level `with-envs` entry), the agent
>   leaves it out of the container instead of passing the App's private key in.
> - GitHub Enterprise Server is not covered either; it keeps using a PAT.

## Why

| | PAT | GitHub App |
|---|---|---|
| Lifetime | Long-lived (until revoked/expired) | 1 hour, minted per run |
| Scope | Everything the user can reach | One repository, App permissions only |
| Tied to a person | Yes — breaks when they leave | No |
| Shows up on PRs as | The user | `your-app[bot]` |
| Can approve PRs the PAT owner authored | No | Yes |
| API rate limit | 5,000/h per user | Scales with installation size |

## Setup

1. **Create a GitHub App** (organization *Settings → Developer settings → GitHub Apps → New*).
   - Webhook: optional (the agent's webhook endpoint is configured separately; leave inactive if you
     already deliver webhooks from the repository/organization).
   - **Repository permissions** — grant what your plugins need. A typical PR-review / automation set:

     | Permission | Access |
     |---|---|
     | Contents | Read & write |
     | Pull requests | Read & write |
     | Issues | Read & write |
     | Metadata | Read (mandatory) |
     | Workflows | Read & write — only if plugins edit `.github/workflows/*` |
     | Checks / Commit statuses | Read & write — only if plugins report status |

2. **Generate a private key** on the App page and download the `.pem` file.
3. **Install the App** on the organization/user that owns the repositories and grant it access to
   each repository the agent should work on.
4. **Put the App credentials in the tenant's `GITHUB-TOKEN` secret** (Studio → Settings → Secrets),
   replacing the PAT. The value is the App ID, a comma, then the private key:

   ```
   123456,-----BEGIN RSA PRIVATE KEY-----
   MIIEpAIBAAKCAQEA...
   -----END RSA PRIVATE KEY-----
   ```

   - The part before the comma is the numeric App ID or the Client ID (`Iv1.…`).
   - The part after it is the full contents of the `.pem` file. Single-line fields are fine:
     literal `
` escapes, or the whole file base64-encoded, are also accepted.
   - A PAT never contains a comma, so the agent tells the two apart by the comma alone.

No `rules.json` change is required — keep the existing entry:

```json
{ "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
```

## How it resolves

When the agent starts an executor container whose `with-envs` declares `GITHUB-TOKEN`:

1. **PAT** (no comma in the value): passed to the container unchanged, as before.
2. **App credentials on a `github.com` repository**: the agent signs an App JWT with the private
   key, looks up the installation for the repository (`GET /repos/{owner}/{repo}/installation`),
   creates a token restricted to that repository (`POST /app/installations/{id}/access_tokens`),
   and passes that token to the container as `GITHUB-TOKEN`. The private key never reaches a
   container.
3. **App credentials, but the run does not target a `github.com` repository** (for example an
   Azure DevOps run that inherits a rule-set-level `GITHUB-TOKEN`): the variable is left out.
4. If a token cannot be minted (malformed value, invalid key, App not installed on the
   repository, repository not granted, GitHub unreachable), **the run stops**. Configuration
   errors fail immediately; transient ones (network, GitHub 5xx, rate limiting) are retried by
   the activity's retry policy.

The container env summary log shows the source as `github-app:installation=<id>`; neither the
token nor the private key is ever logged.

## Migration from a PAT

1. Create and install the App (steps above).
2. Replace the `GITHUB-TOKEN` value in the vault with `<app-id>,<private-key>`.
3. Run a webhook-triggered or chat execution and confirm the env summary shows `github-app:…`
   and PR comments/reviews appear as `your-app[bot]`. If something is wrong the run fails with the
   App error.
4. Revoke the old PAT on GitHub. To roll back, put a PAT back into `GITHUB-TOKEN`.

## Limitations

- **One repository per token.** Plugins that clone or call *other* private repositories during a run
  (for example a private plugin marketplace) need those repositories in the same installation, and
  today the token is scoped to the target repository only. Public repositories (including the official
  marketplace) are unaffected.
- **No user identity.** `gh api user` and the GraphQL `viewer` field do not work with installation
  tokens; plugins should not rely on "who am I" for GitHub.
- **Token lifetime is one hour.** Keep `CONTAINER-EXECUTION-TIMEOUT-SECONDS` below ~55 minutes; the
  agent logs a warning when it is not.
- **github.com only.** GitHub Enterprise Server hosts keep using a PAT.
- **Per-tenant App.** Each tenant brings its own App identity through its vault. A single shared
  platform App would need an installation → tenant binding to keep tenants isolated, and is not
  implemented.
- **Azure DevOps is unchanged** and still uses the `AZURE-DEVOPS-TOKEN` PAT, so Azure DevOps comments
  and votes still appear as the PAT owner (see the note at the top).

# Webhook headers — guide

This document explains how inbound HTTP headers reach `OrchestrateAsync` alongside name, payload, and tenant, and how optional webhook verification uses those headers at ingress.

---

## Overview

When external systems POST to the **builtin** webhook endpoint, all inbound HTTP headers (e.g. `X-GitHub-Event`, `X-Hub-Signature-256`) are forwarded to the agent as `WebhookContext.Metadata` and passed into the orchestrator:

```csharp
// XianixAgent.cs
var batch = await orchestrator.OrchestrateAsync(
    context.Webhook.Name,
    context.Webhook.Payload,
    context.Webhook.TenantId,
    context.Metadata,
    cancellationToken);
```

Only the builtin path forwards headers. The legacy Temporal Update webhook (`POST /api/user/webhooks/{workflow}/{methodName}`) does not.

Before orchestration, `XianixAgent` runs `WebhookVerificationGate` against the same `context.Metadata` dictionary. Verification is optional and provider-specific — see [Webhook verification](#webhook-verification) below.

---

## Webhook context fields

| Source       | Property                                 | Example use                                 |
| ------------ | ---------------------------------------- | ------------------------------------------- |
| Query        | `context.Webhook.Name`                   | Event type label (`pull_request_opened`)    |
| Body         | `context.Webhook.Payload`                | JSON string for rules                       |
| Auth         | `context.Webhook.TenantId`               | Multi-tenant isolation                      |
| Query        | `context.Webhook.ParticipantId`, `Scope` | Threading / scoping                         |
| Query        | `context.Webhook.Authorization`          | Optional downstream token (not the API key) |
| Generated    | `context.Webhook.RequestId`              | Correlation                                 |
| HTTP headers | `context.Metadata`                       | Signatures, event type, delivery IDs, etc.  |

**Outbound** response headers work independently: set them on `WebhookResponse` via `context.Respond(...)` and the server applies them to the HTTP response.

---

## Pipeline

```text
External POST with headers
        │
        ▼
WebhookEndpoints  ──► WebhookHeaderCapture (all inbound headers)
        │
        ▼
MessageService    ──► ChatOrDataRequest.Metadata + Temporal signal Metadata
        │
        ▼
MessageProcessor  ──► ProcessMessageActivityRequest.Metadata
        │
        ▼
WebhookContext    ──► context.Metadata
        │
        ▼
XianixAgent       ──► WebhookVerificationGate (optional, provider-specific)
        │
        ▼
XianixAgent       ──► OrchestrateAsync(..., context.Metadata)
```

### Server capture behavior

**File:** `XiansAI/XiansAi.Server/.../WebhookHeaderCapture.cs`

- All inbound request headers are captured and placed on `ChatOrDataRequest.Metadata` (`Dictionary<string, string>`).
- Multiple values for one header: the **first** non-empty value is used.
- Header names keep the casing provided by ASP.NET request headers.
- Metadata is sent on the Temporal signal only; it is **not** stored in MongoDB conversation messages.

### Server → Temporal signal

**File:** `MessageService.SignalWorkflowAsync`

- `request.Metadata` is included in the anonymous signal payload object.

### Xians.Lib → activity → handler

| File                                    | Role                                                    |
| --------------------------------------- | ------------------------------------------------------- |
| `InboundMessagePayload`                 | `Metadata` property on the signal payload               |
| `MessageProcessor`                      | `activityRequest.Metadata = message.Payload.Metadata`   |
| `ProcessMessageActivityRequest`         | Carries `Metadata` into the activity                    |
| `MessageActivities.ProcessWebhookAsync` | Passes `request.Metadata` into `ActivityWebhookContext` |
| `WebhookContext.Metadata`               | Available in `OnWebhook` handlers                       |

### the-agent

**File:** `TheAgent/Rules/WebhookVerificationGate.cs`

`IEventOrchestrator.OrchestrateAsync` accepts an optional `headers` argument (`IReadOnlyDictionary<string, string>?`). `EventOrchestrator` logs inbound header keys at Debug level. Rules evaluation (`WebhookRulesEvaluator`) still uses webhook name and payload only — headers are not part of match expressions today.

---

## Webhook verification

`WebhookVerificationGate` runs on the integrator ingress path in `XianixAgent.ConfigureWebhookWorkflow` **before** `OrchestrateAsync`. It:

1. Loads `rules.json` and finds the rule set whose `webhook` name matches `context.Webhook.Name` (case-insensitive).
2. Detects the inbound provider (GitHub vs Azure DevOps) from headers and payload.
3. Runs the matching verifier **only when that provider's secret field is configured** on the matched rule set.

Secrets are resolved from the tenant Secret Vault via `XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)`. Each `*-verification-secret` field in `rules.json` is a **vault key name** (not a `secrets.*` prefix).

### Provider detection

Detection order in `WebhookProviderDetector`:

| Priority | Signal | Provider |
| -------- | ------ | -------- |
| 1 | `X-Hub-Signature-256` header present | GitHub |
| 2 | JSON payload has non-empty string `eventType` | Azure DevOps |
| 3 | `X-GitHub-Event` header present | GitHub |
| — | None of the above | Unknown (verification skipped) |

If both GitHub signature and Azure DevOps `eventType` are present, GitHub wins because the signature header is checked first.

### Verification methods

| Provider | `rules.json` fields | Check |
| -------- | ------------------- | ----- |
| GitHub | `github-webhook-verification-secret` | HMAC-SHA256 over the **raw payload** string; compared to `X-Hub-Signature-256` (`sha256=<hex>`) |
| Azure DevOps | `azuredevops-webhook-verification-secret` + optional `azuredevops-webhook-verification-header` | Shared secret sent in a custom HTTP header (default header name: `X-Hook-Secret`) |

Header lookup is **case-insensitive** for both providers (`WebhookHeaderHelpers.TryGetHeaderValue`).

#### Why `azuredevops-webhook-verification-header` is separate

GitHub always sends `X-Hub-Signature-256` — the header name is fixed, so only `github-webhook-verification-secret` is needed.

Azure DevOps Service Hooks let operators choose **any** header name in `httpHeaders` (e.g. `X-Hook-Secret`, `X-DevOps-Secret`). The agent needs:

- `azuredevops-webhook-verification-secret` — vault key for the expected secret **value**
- `azuredevops-webhook-verification-header` — which HTTP header carries that value (defaults to `X-Hook-Secret` when omitted or blank)

### Outcomes

| Result | When | Agent behavior |
| ------ | ---- | -------------- |
| **Skipped** | No rules, no matching rule set, unknown provider, or provider detected but that provider's secret field is empty/absent | Debug log with reason; orchestration continues |
| **Passed** | Provider detected, secret configured, check succeeds | Information log; orchestration continues |
| **Failed** | Provider detected, secret configured, check fails (or vault returns no value for the configured key) | Warning log with reason; responds `{ status: "ignored", reason: "Webhook could not be verified." }`; orchestration skipped |

Provider-specific secrets are independent: an Azure DevOps payload with only `github-webhook-verification-secret` configured skips ADO verification (`no-verification-secret-configured-for-azuredevops`), and vice versa.

#### Skip reasons (`WebhookVerificationStatus.Skipped`)

| Reason | Meaning |
| ------ | ------- |
| `no-rules-defined` | `rules.json` is empty or failed to load |
| `no-matching-rule-set` | No rule set matches `context.Webhook.Name` |
| `unknown-webhook-provider` | Provider could not be detected |
| `no-verification-secret-configured-for-github` | GitHub detected, `github-webhook-verification-secret` not set |
| `no-verification-secret-configured-for-azuredevops` | Azure DevOps detected, `azuredevops-webhook-verification-secret` not set |

#### Failure reasons (`WebhookVerificationStatus.Failed`)

| Reason | Provider | Meaning |
| ------ | -------- | ------- |
| `verification-secret-unavailable` | Both | Secret key is configured in `rules.json` but the vault returned null/empty |
| `missing-signature-header` | GitHub | `X-Hub-Signature-256` not found in `context.Metadata` |
| `invalid-signature-format` | GitHub | Header present but not `sha256=<hex>` |
| `signature-mismatch` | GitHub | HMAC does not match the vault secret |
| `missing-verification-header` | Azure DevOps | Configured header name not found in `context.Metadata` |
| `verification-secret-mismatch` | Azure DevOps | Header present but value does not match the vault secret |

#### Success reasons (`WebhookVerificationStatus.Passed`)

| Reason | Provider |
| ------ | -------- |
| `signature-verified` | GitHub |
| `verification-header-verified` | Azure DevOps |

`XianixAgent.LogWebhookVerificationFailure` emits provider-specific warning messages for the four well-known failure constants above; other failure reasons use a generic warning template.

---

## Repositories and ownership

| Layer                                       | Repository                 |
| ------------------------------------------- | -------------------------- |
| HTTP capture + `ChatOrDataRequest.Metadata` | XiansAI (`XiansAi.Server`) |
| Temporal / `WebhookContext` plumbing        | XiansAI (`XiansAi.Lib`)    |
| Header verification + business use          | Xianix (`the-agent`)       |

The UI does not need changes for inbound webhook headers.

---

## How to verify

### Configure `rules.json`

```json
{
  "webhook": "Default",
  "github-webhook-verification-secret": "GITHUB-WEBHOOK-SECRET",
  "azuredevops-webhook-verification-secret": "ADO-WEBHOOK-SECRET",
  "azuredevops-webhook-verification-header": "X-Hook-Secret",
  "executions": [ /* ... */ ]
}
```

Each `*-verification-secret` field is a **vault key name** in Agent Studio (not a `secrets.*` prefix). Verification settings are per rule set — the `webhook` name must match the builtin endpoint's `webhookName` query parameter.

### GitHub setup

1. Set `github-webhook-verification-secret` to your vault key (e.g. `GITHUB-WEBHOOK-SECRET`).
2. Add the same value as the webhook secret in GitHub repo webhook settings.
3. Store the value in Agent Studio under that vault key.
4. GitHub signs the raw request body; the agent verifies against `context.Webhook.Payload` as received (no re-serialization).

### Azure DevOps setup

1. In **Project Settings → Service hooks**, create or edit the webhook subscription.
2. Under **HTTP headers**, add e.g. `X-Hook-Secret:<value>`.
3. Store `<value>` in Agent Studio under vault key `ADO-WEBHOOK-SECRET` (or your chosen key).
4. Set `azuredevops-webhook-verification-header` to the same header key (omit to use default `X-Hook-Secret`).

### Runtime checks

1. Run server + agent worker.
2. POST to the **builtin** webhook endpoint with `webhookName` matching a rule set in `rules.json`.
3. Valid request with configured secret → logs show `Webhook verification passed`, orchestration proceeds.
4. No secret configured for the detected provider → `Webhook verification skipped` at Debug level with a `no-verification-secret-configured-for-*` reason.
5. Bad/missing signature or header → verification failed warning, HTTP response `status: ignored`, workflow skipped.
6. Secret key configured but missing from vault → `verification-secret-unavailable` failure.

Unit tests in `TheAgent.Tests/Rules/WebhookVerificationGateTests.cs` cover provider detection, skip/pass/fail paths, case-insensitive ADO headers, and custom ADO header names.

---

## Further reading

- [architecture.md](./architecture.md) — agent-centric Mermaid flows
- [azure-devops-pr-label-webhooks.md](./azure-devops-pr-label-webhooks.md) — end-to-end ADO webhook walkthrough
- `XiansAI/XiansAi.Server/XiansAi.Server.Src/docs/WEBHOOKS.md` — platform webhook API and header behavior
- `TheAgent/Rules/WebhookVerificationGate.cs` — verification implementation
- `TheAgent/Agent/XianixAgent.cs` — webhook handler and verification gate wiring

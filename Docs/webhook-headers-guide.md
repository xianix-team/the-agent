# Webhook headers — guide

This document explains how inbound HTTP headers reach `OrchestrateAsync` alongside name, payload, and tenant.

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

`IEventOrchestrator.OrchestrateAsync` accepts an optional `headers` argument (`IReadOnlyDictionary<string, string>?`). `EventOrchestrator` logs inbound header keys at Debug level. Rules evaluation (`WebhookRulesEvaluator`) currently uses webhook name and payload only — header-based matching would require extending `rules.json` or the evaluator.

---

## Repositories and ownership


| Layer                                       | Repository                 |
| ------------------------------------------- | -------------------------- |
| HTTP capture + `ChatOrDataRequest.Metadata` | XiansAI (`XiansAi.Server`) |
| Temporal / `WebhookContext` plumbing        | XiansAI (`XiansAi.Lib`)    |
| Business use of headers                     | Xianix (`the-agent`)       |


The UI does not need changes for inbound webhook headers.

---

## How to verify

1. Run server + agent worker (`TheAgent.csproj` uses a project reference to `Xians.Lib` in the monorepo, or `Xians.Lib` 3.24.4+ as a NuGet package).
2. POST to the builtin webhook with a custom header, e.g. `X-GitHub-Event: push`.
3. Log or breakpoint in `OnWebhook` — `context.Metadata["X-GitHub-Event"]` should equal `push`.
4. Confirm `OrchestrateAsync` receives the same dictionary (Debug log lists header keys only).

---

## Further reading

- [architecture.md](./architecture.md) — agent-centric Mermaid flows
- `XiansAI/XiansAi.Server/XiansAi.Server.Src/docs/WEBHOOKS.md` — platform webhook API and header behavior
- `Xianix/the-agent/TheAgent/Agent/XianixAgent.cs` — webhook handler


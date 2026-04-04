# Xianix Agent Architecture

## Architecture objectives

- Be centered around the Git repository for initial set of agents
- Ability to create sophisticated agents
- Agent should be centrally governed, not on the IDE of a developer
- Easily bring in custom agents to extend the agent architecture ***
- Support different CM platforms like gitHub, gitlabs, Azure Devops ***
- Isolation levels (tenant, repo) ***
- Platform to support non-coding agents such as for marketing, HR, etc.
- Monitoring, visibility and quota allocations
- Human<->Agent ping-pong process
- Quality over cost??




## Overall Flow

### Agent Activated

Configuration: rules.json
Workflows inputs: Repo Git URL

start XianixWorkflow -> Create Isolated Workspace for tenant

### Event occurred

GitHub/Azure DevOps -> Xians ACP -> Agent DotNet Console Application -> Orchastrator

Orchastrator -> Via Rule.json (Fileter Events, Extract Inputs, Identify Claude Code Plugin to Invoke) -> Signal XianixWorkflow

XianixWorkflow Signal -> Clone repository -> Install Claude Code Plugins -> Invoke Claude Code Plugin

---

## Diagrams

### Agent Activation Flow

```mermaid
flowchart TD
    START([Start .NET App]) --> INIT[XiansPlatform.InitializeAsync]
    INIT --> REG[Register XianixAgent]
    REG --> KNOW[Upload rules.json as Knowledge]
    KNOW --> WFS[Define Workflows]
    WFS --> CONV[ConversationalWorkflow\nSupervisor — Chat Messages]
    WFS --> HOOK[WebhookWorkflow\nIntegrator — Webhook Events]
    CONV --> MAF[Bind MafSubAgent\nLLM Chat Handling]
    HOOK --> ORCH[Bind EventOrchestrator\nWebhook Handling]
    MAF & ORCH --> RUN[RunAllAsync]
    RUN --> READY([Agent Ready])
```

---

### Webhook Event Flow

```mermaid
flowchart TD
    GH[GitHub / Azure DevOps] -->|POST webhook| ACP[Xians ACP]
    ACP -->|OnWebhook context| WH[WebhookWorkflow]
    WH --> ORCH[EventOrchestrator.OrchestrateAsync]
    ORCH --> EVAL[RulesEvaluator.EvaluateAsync\nFilter · Extract Inputs · Identify Plugin]
    EVAL --> MATCH{Rule matched?}
    MATCH -- No --> IGN[Return: ignored]
    MATCH -- Yes --> SIG[Signal XianixWorkflow\nwith extracted inputs]
    SIG --> WS[Create Isolated Workspace]
    WS --> CLONE[Clone Repository]
    CLONE --> INST[Install Claude Code Plugins]
    INST --> INVOKE[Invoke Claude Code Plugin]
    INVOKE --> DONE([Done])
```

---

### Sequence: Webhook Event End-to-End

```mermaid
sequenceDiagram
    autonumber
    participant GH as GitHub / AzDO
    participant ACP as Xians ACP
    participant WH as WebhookWorkflow
    participant ORCH as EventOrchestrator
    participant RULES as RulesEvaluator
    participant WF as XianixWorkflow
    participant CC as Claude Code Plugin

    GH->>ACP: POST webhook (e.g. PR opened)
    ACP->>WH: OnWebhook(context)
    WH->>ORCH: OrchestrateAsync(name, payload, tenantId)
    ORCH->>RULES: EvaluateAsync(webhookName, payload)

    alt No matching rule
        RULES-->>ORCH: null
        ORCH-->>WH: Ignored
        WH-->>ACP: { status: "ignored" }
    else Rule matched
        RULES-->>ORCH: inputs map
        ORCH-->>WH: Matched(inputs)
        WH-->>ACP: { status: "success" }
        WH->>WF: Signal(inputs)
        WF->>WF: Create workspace · Clone repo · Install plugins
        WF->>CC: Invoke plugin
        CC-->>WF: Result
    end
```

---

### Sequence: Chat Message Handling

```mermaid
sequenceDiagram
    autonumber
    participant USER as User
    participant CONV as ConversationalWorkflow
    participant KNO as Knowledge Store
    participant MAF as MafSubAgent (LLM)

    USER->>CONV: Send chat message
    CONV->>KNO: GetAsync("Welcome Message")
    KNO-->>CONV: knowledge content
    CONV->>MAF: RunAsync(context)
    MAF-->>CONV: response
    CONV->>USER: ReplyAsync(response)
```

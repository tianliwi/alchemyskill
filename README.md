# Alchemy Skill

Alchemy Skill exposes Alchemy's curated branching troubleshooting solutions as a session-based API suitable for Microsoft 365 Copilot and other conversational clients.

The API recursively loads redirected branches, resolves Alchemy display resources, normalizes the complete solution graph, and guides users through deterministic troubleshooting one step at a time.

## Features

- Searches Alchemy using a natural-language issue description.
- Recursively follows `redirectnode` branches.
- Resolves referenced `alchemyresource` content.
- Normalizes Alchemy graphs into a stable API model.
- Stores troubleshooting sessions in local SQLite.
- Pins sessions to immutable compressed solution snapshots.
- Validates choices and optimistic session revisions.
- Supports session resume, escalation, and AI-safe solution context.
- Preserves the original exploratory Alchemy proxy endpoints.

## Architecture

```text
Copilot or API client
        |
Troubleshooting API
  - Session service
  - Graph traversal
  - Resource resolution
  - Transition validation
        |
        +-- SQLite session store
        +-- Compressed solution snapshots
        |
Alchemy APIs
```

Alchemy controls approved instructions and graph transitions. An AI client can interpret natural-language responses and submit valid choices, but it cannot create arbitrary transitions.

## Requirements

- .NET 9 SDK
- Network access to `https://alchemy.microsoft.com`

## Run locally

```powershell
dotnet restore
dotnet run --project .\AlchemyProxy.csproj --urls http://127.0.0.1:5080
```

Local data is created under `.local\`:

```text
.local\
  troubleshooting.db
  solutions\
```

The directory is excluded from Git.

## Start a troubleshooting session

```powershell
$body = @{
  query = "Can't start outlook"
  locale = "en-us"
  client = "m365-copilot"
  context = @{
    application = "Outlook"
  }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:5080/api/v1/troubleshooting/sessions" `
  -ContentType "application/json" `
  -Body $body
```

Example response:

```json
{
  "sessionId": "019f...",
  "status": "inProgress",
  "revision": 1,
  "step": {
    "type": "choice",
    "question": "Before we can troubleshoot the Outlook startup problem, please select the device Outlook is on:",
    "choices": [
      {
        "id": "...",
        "label": "PC (Windows)"
      },
      {
        "id": "...",
        "label": "Mac"
      }
    ]
  }
}
```

## Submit an answer

Use the graph ID, node ID, choice ID, and revision returned by the current step:

```powershell
$body = @{
  step = @{
    graphId = "<current-graph-id>"
    nodeId = "<current-node-id>"
  }
  choiceId = "<selected-choice-id>"
  expectedRevision = 1
  choiceSource = "explicit"
  userMessage = "PC"
} | ConvertTo-Json -Depth 5

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:5080/api/v1/troubleshooting/sessions/<session-id>/answers" `
  -ContentType "application/json" `
  -Body $body
```

## API endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/troubleshooting/sessions` | Start a troubleshooting session |
| `GET` | `/api/v1/troubleshooting/sessions/{sessionId}` | Resume or inspect a session |
| `POST` | `/api/v1/troubleshooting/sessions/{sessionId}/answers` | Submit a valid choice |
| `POST` | `/api/v1/troubleshooting/sessions/{sessionId}/escalations` | Escalate an unresolved session |
| `GET` | `/api/v1/troubleshooting/sessions/{sessionId}/context` | Get normalized AI-safe solution context |

## Local test identity

Authentication is not yet enabled. Local requests can identify a test owner using:

```http
X-Test-Tenant-Id: local-tenant
X-Test-User-Id: local-user
```

When omitted, both values use the defaults shown above.

## Error handling

Errors use `application/problem+json`. Important responses include:

- `404 branching_solution_not_found`
- `409 stale_session_revision`
- `409 step_mismatch`
- `409 session_not_active`
- `422 invalid_choice`
- `422 invalid_solution_graph`
- `502 alchemy_invalid_response`
- `503 alchemy_unavailable`

## Documentation

- [Copilot troubleshooting design](alchemy-copilot-troubleshooting-design.md)
- [API implementation specification](alchemy-troubleshooting-api-implementation-spec.md)

## Current status

The local C# implementation supports complete graph expansion and deterministic session traversal. The `Can't start outlook` scenario has been exercised through Windows, Microsoft 365, unsuccessful remediation, escalation, and process restart with SQLite persistence.

Production authentication, distributed storage, idempotency records, and Microsoft 365 Copilot packaging remain future work.

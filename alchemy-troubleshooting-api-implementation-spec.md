# Alchemy Troubleshooting API - Implementation Specification

## 1. Purpose

Build a service that converts Alchemy branching solutions into a stable, session-based troubleshooting API for Microsoft 365 Copilot.

The service must:

- Search Alchemy using a user's issue description.
- Recursively load all redirected branching solutions.
- Resolve every referenced Alchemy display resource.
- Validate and normalize the resulting graph.
- Maintain deterministic troubleshooting session state.
- Return one actionable step at a time.
- Allow an AI agent to interpret natural-language responses while preventing invalid graph transitions.
- Preserve enough normalized solution context for the future AI Troubleshooting Skill.

## 2. Validated Alchemy Behavior

The following upstream APIs are sufficient to traverse a complete solution.

### 2.1 Search for the initial solution

```http
POST https://alchemy.microsoft.com/api/v2/insights/app/servicenowvirtualagent
Content-Type: application/json
```

```json
{
  "Text": "Can't start outlook"
}
```

The response contains a branching insight:

```json
{
  "Insights": [
    {
      "SolutionType": "Branching",
      "Description": "{ ...serialized branching graph... }"
    }
  ]
}
```

`Description` is a JSON string and must be parsed as a second JSON document.

### 2.2 Resolve an Alchemy display resource

```http
GET https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/alchemyresource/{resourceId}
```

The response shape is:

```json
{
  "SolutionTitle": " ",
  "SolutionContent": "<div>Display content...</div>",
  "CorrelationId": "...",
  "SolutionMetadata": null
}
```

`SolutionContent` usually contains HTML intended for display to the user.

### 2.3 Resolve a redirected branch

```http
GET https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/branching/{branchId}
```

The response shape is:

```json
{
  "SolutionTitle": "",
  "SolutionContent": "{ ...serialized branching graph... }",
  "CorrelationId": "...",
  "SolutionMetadata": null
}
```

`SolutionContent` is a JSON string and must be parsed as a branching graph.

### 2.4 Traversal validation

The query `Can't start outlook` produced:

- 6 branching graphs.
- 31 total nodes.
- 15 unique Alchemy display resources.
- 5 followed redirects.
- No branch or resource fetch failures.

The root graph disambiguates between Mac and PC. The Windows branch can redirect to Office 2013, Office 2016, and Office 2019 end-of-support graphs.

## 3. System Context

```text
Microsoft 365 Copilot
        |
        | OAuth 2.0 / Entra ID
        v
Troubleshooting API
  +-----------------------------+
  | Session Controller          |
  | Session Service             |
  | Solution Loader             |
  | Graph Expander              |
  | Resource Resolver           |
  | Graph Validator             |
  | Content Normalizer          |
  +-----------------------------+
        |                 |
        |                 +------ Session Store
        |
        +------------------------ Solution Cache
        |
        v
Alchemy APIs
```

## 4. Service Components

### 4.1 Alchemy client

Responsibilities:

- Call the three Alchemy endpoints.
- Apply request timeouts.
- Propagate cancellation.
- Retry only transient failures.
- Capture Alchemy correlation IDs.
- Deserialize wrapper responses.
- Reject malformed or incomplete responses.

Recommended operations:

```text
SearchInsightsAsync(query, locale, cancellationToken)
GetBranchAsync(branchId, locale, cancellationToken)
GetResourceAsync(resourceId, locale, cancellationToken)
```

The client must not contain graph traversal or session behavior.

### 4.2 Solution loader

Responsibilities:

- Search for the initial solution.
- Select the branching insight.
- Parse its serialized `Description`.
- Initiate recursive graph expansion.
- Resolve all resources.
- Validate the assembled snapshot.
- Produce a normalized immutable solution.

### 4.3 Graph expander

Responsibilities:

- Start with the root branching graph.
- Find every `redirectnode`.
- Read its `targetDialogId`.
- Fetch each referenced child graph.
- Recursively inspect child graphs for additional redirects.
- Deduplicate branch IDs.
- Detect cycles and excessive traversal.

The implementation should use a queue rather than recursive call-stack traversal.

```text
graphs[rootGraph.id] = rootGraph
queue = rootGraph.redirectTargets

while queue is not empty:
    branchId = queue.dequeue()

    if branchId is already loaded:
        continue

    childGraph = fetch branchId
    graphs[branchId] = childGraph

    enqueue childGraph redirect targets
```

Required traversal limits:

| Limit | Initial value |
|---|---:|
| Maximum graphs | 100 |
| Maximum nodes across graphs | 5,000 |
| Maximum resources | 2,000 |
| Maximum redirect depth | 25 |
| Maximum expanded payload | 25 MB |

Exceeding a limit must fail solution loading with an explicit error. The service must not return a partially expanded solution as complete.

### 4.4 Resource resolver

The resolver must recursively inspect graph values for objects with:

```json
{
  "contentType": "alchemyresource",
  "content": "{resourceId}"
}
```

Responsibilities:

- Collect unique resource IDs across all graphs.
- Fetch resources with bounded concurrency.
- Deduplicate repeated resource references.
- Preserve the Alchemy correlation ID.
- Sanitize returned HTML.
- Produce model-safe text and display-safe content.

Recommended normalized representation:

```json
{
  "resourceId": "b17d3763-b13e-4c8a-a72b-23fbad6b1b8b",
  "contentType": "text/html",
  "html": "<div><span>Great! Glad we were able to help.</span></div>",
  "plainText": "Great! Glad we were able to help.",
  "links": [],
  "correlationId": "..."
}
```

The sanitizer must remove:

- Scripts.
- Event-handler attributes.
- Embedded objects.
- Unsafe URL schemes.
- Unsupported forms.
- Unapproved inline behavior.

HTML sanitization failures must be surfaced as solution-loading errors. Unsanitized content must never be returned.

### 4.5 Graph validator

Validation occurs after all graphs and resources have been loaded.

Required checks:

- Every graph has a nonempty `meta.id`.
- Every graph has a valid `startNodeId`.
- Node IDs are unique within a graph.
- Every `staticChoices[].targetNodeId` exists in the same graph.
- Every redirect has a nonempty `targetDialogId`.
- Every redirect target graph was loaded.
- Every Alchemy resource reference was resolved.
- Node types are supported.
- Choice IDs are unique within a node.
- Terminal nodes do not require an outgoing transition.
- Redirect cycles are detected.
- Actionable traversal from each graph's start node terminates or enters an explicitly allowed loop.

Initially supported node types:

```text
choicenode
textnode
redirectnode
```

Unknown node types must cause explicit validation failure until support is implemented.

### 4.6 Content normalizer

The normalizer converts Alchemy-specific content into a stable API model.

Supported source content types:

| Alchemy type | Normalized behavior |
|---|---|
| `embeddedplaintext` | Return its text directly |
| `alchemyresource` | Replace with resolved sanitized content |
| `null` | Omit the optional field |

The normalized representation should preserve:

- Original node and graph IDs.
- Original resource IDs.
- Exact choice IDs.
- Exact transition targets.
- Outcome signals.
- Display text.
- Plain text suitable for an LLM.
- Source correlation IDs.

## 5. Normalized Solution Model

An expanded solution is an immutable snapshot.

```json
{
  "solutionId": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
  "version": "sha256:...",
  "locale": "en-us",
  "title": "Can't start Outlook",
  "rootGraphId": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
  "loadedAt": "2026-07-23T22:20:00Z",
  "graphs": {
    "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c": {
      "id": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
      "title": "Can't start Outlook",
      "startNodeId": "30fbf5b3-4495-464f-a62f-65cbff3f2a69",
      "nodes": {}
    }
  }
}
```

The version should be a deterministic hash of the normalized graph and resource content. It pins active sessions to the exact content they started with.

### 5.1 Normalized choice node

```json
{
  "id": "node-1",
  "type": "choice",
  "name": "Operating system",
  "instruction": {
    "plainText": "Select the device where Outlook cannot start.",
    "html": null,
    "resourceId": null
  },
  "question": "Are you using a Mac or PC?",
  "choices": [
    {
      "id": "choice-mac",
      "label": "Mac",
      "targetNodeId": "redirect-mac"
    },
    {
      "id": "choice-pc",
      "label": "PC",
      "targetNodeId": "redirect-pc"
    }
  ],
  "outcome": null
}
```

### 5.2 Normalized text node

```json
{
  "id": "success",
  "type": "text",
  "name": "Success",
  "instruction": {
    "plainText": "Great! Glad we were able to help.",
    "html": "<div>Great! Glad we were able to help.</div>",
    "resourceId": "..."
  },
  "outcome": "successful"
}
```

### 5.3 Normalized redirect node

```json
{
  "id": "redirect-pc",
  "type": "redirect",
  "name": "Redirect to Windows solution",
  "targetGraphId": "cf0bea24-38ea-47bd-9253-ca9b586107ca"
}
```

## 6. Session Model

```json
{
  "sessionId": "01J...",
  "userId": "entra-object-id",
  "tenantId": "entra-tenant-id",
  "query": "Can't start outlook",
  "locale": "en-us",
  "solutionId": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
  "solutionVersion": "sha256:...",
  "currentGraphId": "cf0bea24-38ea-47bd-9253-ca9b586107ca",
  "currentNodeId": "a001d470-6f6f-4d68-8af5-9015f7b73dfd",
  "status": "inProgress",
  "revision": 3,
  "createdAt": "2026-07-23T22:20:00Z",
  "updatedAt": "2026-07-23T22:22:00Z",
  "expiresAt": "2026-07-24T22:20:00Z",
  "history": []
}
```

Supported statuses:

```text
inProgress
resolved
unresolved
escalated
expired
failed
```

Each history entry records:

```json
{
  "sequence": 2,
  "graphId": "...",
  "nodeId": "...",
  "choiceId": "...",
  "choiceSource": "explicit",
  "confidence": null,
  "evidence": "User selected PC.",
  "timestamp": "2026-07-23T22:21:00Z"
}
```

`choiceSource` values:

```text
explicit
ai-inferred
ai-confirmed
system
```

## 7. Public API

Base path:

```text
/api/v1/troubleshooting
```

All endpoints require an authenticated Microsoft Entra ID user unless explicitly designated as a health endpoint.

### 7.1 Start a troubleshooting session

```http
POST /api/v1/troubleshooting/sessions
```

Request:

```json
{
  "query": "Can't start outlook",
  "locale": "en-us",
  "client": "m365-copilot",
  "context": {
    "operatingSystem": null,
    "deviceType": null,
    "application": "Outlook"
  }
}
```

Validation:

- `query` is required.
- Query length must be between 2 and 2,000 characters.
- Locale must be supported.
- Context fields must use documented size limits.

Response:

```json
{
  "sessionId": "01J...",
  "solution": {
    "id": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
    "version": "sha256:...",
    "title": "Can't start Outlook"
  },
  "status": "inProgress",
  "revision": 1,
  "step": {
    "graphId": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
    "id": "30fbf5b3-4495-464f-a62f-65cbff3f2a69",
    "type": "choice",
    "name": "Operating system",
    "instruction": {
      "plainText": null,
      "html": null
    },
    "question": "Where are you having trouble starting Outlook?",
    "choices": [
      {
        "id": "...",
        "label": "Mac"
      },
      {
        "id": "...",
        "label": "PC"
      }
    ]
  }
}
```

Behavior:

1. Search Alchemy.
2. Select the first valid `Branching` insight.
3. Load or reuse the expanded normalized solution.
4. Create the session.
5. Resolve automatic redirects until an actionable or terminal node is reached.
6. Return the current step.

If multiple branching insights are returned, the first valid result is used initially. The service must record the number and identifiers of other candidates for later solution-selection improvements.

### 7.2 Get or resume a session

```http
GET /api/v1/troubleshooting/sessions/{sessionId}
```

Returns the current status, revision, and actionable step.

The endpoint must verify that the authenticated user and tenant own the session.

### 7.3 Submit an answer

```http
POST /api/v1/troubleshooting/sessions/{sessionId}/answers
```

Request:

```json
{
  "step": {
    "graphId": "8e3b1f4c-8531-49ae-ac26-13a4bad6db7c",
    "nodeId": "30fbf5b3-4495-464f-a62f-65cbff3f2a69"
  },
  "choiceId": "choice-pc",
  "expectedRevision": 1,
  "choiceSource": "ai-inferred",
  "confidence": 0.97,
  "evidence": "The user said they are using a Windows laptop.",
  "userMessage": "I'm on my Windows laptop."
}
```

Required validation:

- Session exists and belongs to the caller.
- Session status is `inProgress`.
- `expectedRevision` matches the stored revision.
- Submitted graph and node match the current step.
- Current node is a choice node.
- Submitted choice exists on the current node.
- AI confidence is between 0 and 1 when supplied.
- Evidence and user message comply with size limits.

Behavior:

1. Record the answer.
2. Transition to the selected choice's `targetNodeId`.
3. Automatically resolve redirects.
4. Stop on the next choice or terminal text node.
5. Update status from the terminal outcome when available.
6. Increment the session revision.
7. Persist atomically.
8. Return the new current step.

Response:

```json
{
  "sessionId": "01J...",
  "status": "inProgress",
  "revision": 2,
  "step": {
    "graphId": "cf0bea24-38ea-47bd-9253-ca9b586107ca",
    "id": "a001d470-6f6f-4d68-8af5-9015f7b73dfd",
    "type": "choice",
    "question": "What version of Office are you using?",
    "choices": [
      {
        "id": "...",
        "label": "Microsoft 365"
      }
    ]
  }
}
```

### 7.4 Escalate a session

```http
POST /api/v1/troubleshooting/sessions/{sessionId}/escalations
```

Request:

```json
{
  "expectedRevision": 4,
  "reason": "User requested additional help.",
  "userMessage": "Can I talk to support?"
}
```

Response:

```json
{
  "sessionId": "01J...",
  "status": "escalated",
  "revision": 5,
  "summary": {
    "issue": "Can't start Outlook",
    "completedSteps": [],
    "observations": [],
    "lastGraphId": "...",
    "lastNodeId": "..."
  }
}
```

### 7.5 Get AI-safe solution context

```http
GET /api/v1/troubleshooting/sessions/{sessionId}/context
```

Purpose:

- Support the future AI Troubleshooting Skill.
- Let the AI understand the complete approved solution.
- Avoid exposing raw Alchemy response wrappers.

Response:

```json
{
  "sessionId": "01J...",
  "solutionId": "...",
  "solutionVersion": "sha256:...",
  "currentGraphId": "...",
  "currentNodeId": "...",
  "constraints": [
    "Use only listed choices for transitions.",
    "Do not invent troubleshooting instructions.",
    "Do not claim success without user confirmation."
  ],
  "graphs": [],
  "history": []
}
```

This endpoint should return plain text, safe links, graph semantics, and IDs required for tool calls. It should not return unsanitized HTML or unrelated internal metadata.

## 8. Transition Algorithm

The session engine operates on a composite position:

```text
(currentGraphId, currentNodeId)
```

Choice targets are resolved within the current graph.

Redirect behavior:

```text
function resolveActionablePosition(graphId, nodeId):
    visitedRedirects = set()

    while true:
        node = solution.graphs[graphId].nodes[nodeId]

        if node is choice:
            return node

        if node is text:
            return node

        if node is redirect:
            key = graphId + ":" + nodeId

            if key is already visited:
                fail with redirect-cycle error

            visitedRedirects.add(key)
            graphId = node.targetGraphId
            nodeId = solution.graphs[graphId].startNodeId
            continue

        fail with unsupported-node error
```

Redirects are automatic system transitions and should be recorded in history with `choiceSource: system`.

## 9. Terminal Outcome Mapping

Alchemy `outcomeSignal` values should be normalized case-insensitively.

| Alchemy outcome | Session status |
|---|---|
| `successful` | `resolved` |
| `unsuccessful` | `unresolved` |
| Missing on a terminal text node | `unresolved` initially |
| Unknown value | validation error |

Some observed success text nodes do not include `outcomeSignal`. A missing signal must not be inferred from the node name alone in the first implementation. Record it as an unspecified terminal outcome and return `unresolved` with a reason such as `terminalOutcomeMissing`.

This policy can be changed after content-quality review.

## 10. Error Contract

Use `application/problem+json`.

Example:

```json
{
  "type": "https://api.example.com/problems/stale-session-revision",
  "title": "The troubleshooting session has changed",
  "status": 409,
  "detail": "Expected revision 2 but the current revision is 3.",
  "instance": "/api/v1/troubleshooting/sessions/01J.../answers",
  "code": "stale_session_revision",
  "correlationId": "..."
}
```

Required errors:

| Status | Code | Condition |
|---:|---|---|
| 400 | `invalid_request` | Request validation failed |
| 401 | `unauthenticated` | Missing or invalid token |
| 403 | `session_access_denied` | Session belongs to another user or tenant |
| 404 | `session_not_found` | Session does not exist |
| 404 | `branching_solution_not_found` | No valid branching insight was returned |
| 409 | `stale_session_revision` | Optimistic concurrency conflict |
| 409 | `step_mismatch` | Submitted step is not current |
| 409 | `session_not_active` | Session is terminal or expired |
| 422 | `invalid_choice` | Choice is not valid for the current node |
| 422 | `invalid_solution_graph` | Alchemy returned an invalid graph |
| 502 | `alchemy_invalid_response` | Upstream payload could not be parsed |
| 503 | `alchemy_unavailable` | Alchemy was unavailable after retries |
| 504 | `alchemy_timeout` | Alchemy exceeded the request deadline |

Do not convert upstream failures into successful empty responses.

## 11. Concurrency and Idempotency

### 11.1 Optimistic concurrency

Every session mutation requires `expectedRevision`.

The storage update must use:

```text
UPDATE session
WHERE session_id = @sessionId
  AND revision = @expectedRevision
```

No row updated means a `409 stale_session_revision`.

### 11.2 Idempotency

Mutation endpoints should accept:

```http
Idempotency-Key: {client-generated-key}
```

The service should persist the key, request hash, and response for the idempotency retention period.

Reusing a key with a different request body must return `409 idempotency_key_reused`.

## 12. Storage

### 12.1 Solution cache

Store immutable expanded solution snapshots keyed by:

```text
(rootSolutionId, locale, versionHash)
```

Also maintain a lookup from normalized query cache key to the latest snapshot.

Recommended initial policy:

- In-memory cache for local development.
- Distributed cache for deployed environments.
- 15-minute search-result TTL.
- 60-minute expanded-solution refresh TTL.
- Active sessions retain their pinned solution snapshot until session expiration.

A refresh failure must not mutate an existing snapshot. Continuing an active session with its already-pinned snapshot is allowed and should be logged.

### 12.2 Session store

Use a durable store with atomic conditional updates.

Required indexed fields:

```text
sessionId
tenantId
userId
status
expiresAt
solutionId
solutionVersion
```

Recommended initial session expiration:

- 24 hours after creation.
- Sliding extension may be added later.

### 12.3 Sensitive data

Store the minimum necessary user text. Support configurable redaction before persistence and telemetry emission.

### 12.4 Local test storage

For local development and testing, use SQLite rather than Cosmos DB. The storage implementation must remain behind repository interfaces so the production provider can be replaced without changing session behavior.

Local files:

```text
.local/
  troubleshooting.db
  solutions/
    en-us/
      {solutionId}/
        {versionHash}.json.gz
```

`.local/` must be excluded from source control.

Recommended SQLite tables:

```sql
CREATE TABLE sessions (
    session_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    query TEXT NOT NULL,
    locale TEXT NOT NULL,
    solution_id TEXT NOT NULL,
    solution_version TEXT NOT NULL,
    solution_snapshot_path TEXT NOT NULL,
    current_graph_id TEXT NOT NULL,
    current_node_id TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    context_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

CREATE INDEX ix_sessions_owner
    ON sessions (tenant_id, user_id, updated_at);

CREATE INDEX ix_sessions_expiration
    ON sessions (expires_at);

CREATE TABLE session_events (
    session_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    graph_id TEXT,
    node_id TEXT,
    choice_id TEXT,
    choice_source TEXT,
    confidence REAL,
    evidence TEXT,
    user_message TEXT,
    created_at TEXT NOT NULL,
    PRIMARY KEY (session_id, sequence),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);

CREATE TABLE idempotency_records (
    session_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    response_status INTEGER NOT NULL,
    response_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    PRIMARY KEY (session_id, idempotency_key),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);
```

Answer processing must use one SQLite transaction:

1. Read the session.
2. Verify ownership, status, and expected revision.
3. Validate the choice against the pinned solution snapshot.
4. Update the session with `WHERE revision = @expectedRevision`.
5. Insert the session event.
6. Insert the idempotency record when a key was supplied.
7. Commit.

If the conditional session update affects no rows, roll back and return `409 stale_session_revision`.

Provide these storage abstractions:

```text
ISessionRepository
ISessionEventRepository
IIdempotencyRepository
ISolutionSnapshotStore
```

Local implementations:

```text
SqliteSessionRepository
SqliteSessionEventRepository
SqliteIdempotencyRepository
FileSolutionSnapshotStore
```

Local startup should:

- Create `.local/` when missing.
- Apply versioned SQLite migrations.
- Enable foreign keys.
- Enable write-ahead logging.
- Delete expired sessions and idempotency records on a scheduled interval.
- Never delete a solution snapshot still referenced by an active session.

Suggested SQLite initialization:

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
```

An optional in-memory SQLite connection may be used for unit and integration tests. Manual and end-to-end testing should use the file-backed database so sessions survive API restarts.

## 13. Upstream Resilience

Recommended initial policies:

| Operation | Timeout | Retries |
|---|---:|---:|
| Search insights | 15 seconds | 2 |
| Fetch branch | 10 seconds | 2 |
| Fetch resource | 10 seconds | 2 |

Retry only:

- Network connection failures.
- HTTP 408.
- HTTP 429 using `Retry-After`.
- HTTP 502, 503, and 504.

Do not retry:

- Authentication failures.
- Other 4xx responses.
- JSON parsing failures.
- Graph validation failures.

Use exponential backoff with jitter. Apply bounded concurrency when loading branches and resources.

## 14. Authentication and Authorization

- Authenticate callers using Microsoft Entra ID.
- Validate issuer, audience, tenant, signature, and token lifetime.
- Derive tenant and user IDs from validated claims.
- Never accept tenant or user ownership from request JSON.
- Require a delegated scope such as:

```text
Troubleshooting.Session.ReadWrite
```

- Restrict AI context access to the owning user and approved Copilot application.
- Apply tenant-level solution and locale policies when required.

## 15. Observability

### 15.1 Correlation

Generate a service correlation ID for every incoming request and capture all Alchemy correlation IDs used during that request.

### 15.2 Structured events

Emit events for:

```text
solution_search_started
solution_search_completed
solution_expansion_started
branch_loaded
resource_loaded
solution_validated
session_started
answer_submitted
redirect_followed
session_resolved
session_unresolved
session_escalated
upstream_failure
graph_validation_failure
```

### 15.3 Metrics

- Alchemy request count, duration, retries, and failures.
- Solution expansion duration.
- Graph, node, redirect, and resource counts.
- Cache hit rate.
- Session start and completion counts.
- Resolution and escalation rates.
- Turns and time to resolution.
- Invalid-choice attempts.
- Stale-revision conflicts.
- AI-inferred choice confidence.

Do not include raw user messages in standard metric dimensions.

## 16. OpenAPI Requirements

The service must publish an OpenAPI document suitable for a Microsoft 365 Copilot API plugin.

Each operation must have:

- A stable `operationId`.
- A concise intent-oriented summary.
- A detailed description explaining when Copilot should use it.
- Explicit request and response schemas.
- Authentication requirements.
- Error responses.
- Examples.

Recommended operation IDs:

```text
startTroubleshooting
getTroubleshootingSession
answerTroubleshootingStep
escalateTroubleshootingSession
getTroubleshootingContext
```

Descriptions must tell Copilot:

- Start a session when the user requests technical troubleshooting.
- Submit only a choice returned by the current step.
- Use context only to understand and explain the approved solution.
- Never create node IDs or choice IDs.

## 17. Testing

### 17.1 Unit tests

- Parse initial branching descriptions.
- Parse child `SolutionContent`.
- Find nested Alchemy resource references.
- Deduplicate resources and branches.
- Follow redirect nodes.
- Detect redirect cycles.
- Reject missing target nodes.
- Reject unsupported node types.
- Sanitize HTML.
- Normalize embedded and resource content.
- Compute stable solution hashes.
- Validate choices and revisions.
- Map terminal outcomes.

### 17.2 Contract tests

Record sanitized representative Alchemy responses for:

- A simple single-graph solution.
- A graph with display resources.
- A graph with redirects.
- Nested redirects.
- Repeated resource references.
- Missing resources.
- Malformed child graph JSON.

Tests should run against fixtures by default. Live Alchemy integration tests should be separately marked and not required for every local test run.

### 17.3 End-to-end acceptance scenario

Use:

```text
Can't start outlook
```

Acceptance criteria:

1. The API loads the root disambiguation graph.
2. It loads the Mac and Windows child graphs.
3. It loads the Office 2013, 2016, and 2019 child graphs.
4. It assembles 6 graphs and 31 nodes for the current upstream content.
5. It resolves all 15 referenced resources for the current upstream content.
6. It returns the root operating-system choice.
7. Selecting PC transitions into the Windows graph.
8. The session can follow a version choice into the correct end-of-support branch.
9. Invalid choices are rejected.
10. Replaying a stale revision is rejected.

Exact graph and resource counts should be maintained only in live integration assertions because Alchemy content can change.

### 17.4 Load tests

Test:

- Concurrent session starts for the same query.
- Cache stampede prevention.
- Concurrent resource expansion.
- Multiple answers submitted for the same revision.
- Large graphs near configured limits.

## 18. Implementation Sequence

### Phase 1: Alchemy ingestion

1. Implement the typed Alchemy client.
2. Parse the initial branching insight.
3. Implement recursive branch loading.
4. Implement resource discovery and loading.
5. Add graph validation.
6. Add normalized immutable solution snapshots.
7. Validate using `Can't start outlook`.

### Phase 2: Deterministic sessions

1. Implement session persistence.
2. Implement start, get, and answer endpoints.
3. Implement redirect transitions.
4. Implement terminal outcome mapping.
5. Add optimistic concurrency and idempotency.
6. Add escalation.

### Phase 3: Copilot integration

1. Publish the OpenAPI contract.
2. Add Entra ID authentication.
3. Configure the declarative agent and API plugin.
4. Add Adaptive Card response templates where appropriate.
5. Validate the complete experience in Microsoft 365 Copilot.

### Phase 4: AI Troubleshooting Skill

1. Add the AI-safe context endpoint.
2. Allow AI-inferred choices with confidence and evidence.
3. Add confirmation thresholds in agent instructions.
4. Add completed-step and observation context.
5. Add solution-switch recommendations.
6. Evaluate inference accuracy and safety.

## 19. Initial Definition of Done

The first production-ready release is complete when:

- The three Alchemy endpoints are integrated.
- Branches and resources are recursively and completely resolved.
- Invalid or partial graphs are rejected.
- Content is sanitized.
- Sessions are durable and user-scoped.
- Start, resume, answer, and escalate operations are implemented.
- Redirects are followed automatically.
- Choices and revisions are validated atomically.
- OpenAPI is usable by a Microsoft 365 Copilot API plugin.
- Entra ID authentication and authorization are enforced.
- Logs, metrics, and correlation IDs are available.
- Unit, contract, integration, and end-to-end tests pass.
- The `Can't start outlook` scenario traverses Mac, Windows, and Office end-of-support branches successfully.

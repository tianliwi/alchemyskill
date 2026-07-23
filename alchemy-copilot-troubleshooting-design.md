# Alchemy Troubleshooting in Microsoft 365 Copilot - Design Specification

## 1. Objective

Integrate Alchemy's curated, deterministic troubleshooting solutions into Microsoft 365 Copilot Chat so users can resolve issues through a natural conversation.

Two approaches are considered:

1. **Deterministic Stepper** - Alchemy controls every transition; Copilot presents each step.
2. **AI Troubleshooting Skill** - Copilot understands the complete solution and intelligently conducts the conversation while remaining constrained to approved Alchemy content.

## 2. Shared Alchemy Solution Model

Alchemy currently returns a solution graph similar to:

```json
{
  "schemaVersion": "1.5",
  "startNodeId": "node-1",
  "nodes": [
    {
      "id": "node-1",
      "type": "choicenode",
      "name": "Check Windows Update",
      "text": {
        "contentType": "alchemyresource",
        "content": "resource-id"
      },
      "question": {
        "content": "Did this solve the problem?"
      },
      "staticChoices": [
        {
          "id": "choice-yes",
          "text": { "content": "Yes" },
          "targetNodeId": "success-node"
        },
        {
          "id": "choice-no",
          "text": { "content": "No" },
          "targetNodeId": "next-node"
        }
      ]
    }
  ]
}
```

Before exposing a solution to Copilot, the service should resolve all `alchemyresource` references into renderable instructions, images, links, warnings, and accessibility text.

## 3. Idea A: Deterministic Stepper

### 3.1 Summary

Copilot acts as the conversational UI. Alchemy owns the session, current node, permitted choices, and all transitions.

Copilot never receives responsibility for navigating the graph.

### 3.2 Architecture

```text
Microsoft 365 Copilot Chat
        |
Declarative Troubleshooting Agent
        | API plugin
Troubleshooting Session API
        |
Alchemy Solution and Resource Services
        |
Session Store + Telemetry
```

### 3.3 Interaction flow

1. User says: "There is no sound."
2. Copilot calls `startTroubleshooting`.
3. Alchemy selects the appropriate solution.
4. The API returns the first step.
5. Copilot presents the instruction and allowed choices.
6. The user selects or states an answer.
7. Copilot submits the corresponding `choiceId`.
8. Alchemy validates the answer and returns the next node.
9. The process continues until success, failure, redirect, or escalation.

### 3.4 API design

#### Start a session

```http
POST /v1/troubleshooting/sessions
```

```json
{
  "query": "no sound",
  "locale": "en-US",
  "client": "m365-copilot",
  "context": {
    "operatingSystem": "Windows 11"
  }
}
```

Response:

```json
{
  "sessionId": "session-123",
  "solutionId": "audio-windows-11",
  "solutionVersion": "1.5",
  "status": "inProgress",
  "step": {
    "id": "node-1",
    "title": "Check Windows Update",
    "instruction": "Install any available Windows updates.",
    "question": "Did this solve the problem?",
    "choices": [
      { "id": "choice-yes", "label": "Yes" },
      { "id": "choice-no", "label": "No" },
      { "id": "choice-skip", "label": "Skip" }
    ]
  }
}
```

#### Submit an answer

```http
POST /v1/troubleshooting/sessions/session-123/answers
```

```json
{
  "stepId": "node-1",
  "choiceId": "choice-no",
  "userMessage": "I installed everything but still have no sound"
}
```

#### Resume a session

```http
GET /v1/troubleshooting/sessions/session-123
```

#### Escalate

```http
POST /v1/troubleshooting/sessions/session-123/escalations
```

### 3.5 Copilot instructions

The agent should be instructed to:

- Present the returned step accurately.
- Ask only the returned question and choices.
- Never invent troubleshooting instructions.
- Never select an answer without sufficient user input.
- Submit only valid choice IDs.
- Preserve warnings and safety instructions.
- Offer escalation when returned by the service.

### 3.6 Advantages

- Maximum determinism and auditability.
- Simple to implement and test.
- Curated content is always followed.
- Easy to reproduce a user session.
- Low hallucination risk.
- Solution updates require no prompt changes.

### 3.7 Limitations

- Can feel like a chatbot wrapper around a decision tree.
- Repeats questions even when the user already supplied the answer.
- Limited ability to adapt explanations.
- Cannot easily reason across multiple steps.
- Less effective with ambiguous or unstructured replies.

## 4. Idea B: AI Troubleshooting Skill

### 4.1 Summary

Copilot receives a normalized representation of the complete Alchemy solution. It develops an understanding of the troubleshooting strategy and conducts the conversation dynamically.

Alchemy still validates transitions and remains the source of approved content.

### 4.2 Architecture

```text
Microsoft 365 Copilot Chat
        |
AI Troubleshooting Agent / Skill
  +-------------------------+
  | Conversational state    |
  | Solution understanding  |
  | Choice interpretation   |
  +------------+------------+
               |
               | constrained actions
Troubleshooting Session API
        |
Alchemy Graph Validator
        |
Solution Store + Session Store + Telemetry
```

### 4.3 Solution-understanding phase

At session start, the service returns a normalized graph containing:

- Solution purpose and applicability.
- Resolved instructions.
- Node descriptions.
- Questions and permitted choices.
- Preconditions.
- Expected observations.
- Success and failure outcomes.
- Redirect and escalation rules.
- Safety classifications.

Example:

```json
{
  "solutionId": "audio-windows-11",
  "summary": "Diagnoses missing audio output on Windows 11.",
  "constraints": [
    "Do not invent driver download locations.",
    "Confirm success with the user."
  ],
  "nodes": [
    {
      "id": "unmute",
      "purpose": "Determine whether output is muted.",
      "instruction": "Check the volume and mute controls.",
      "question": "Do you hear sound now?",
      "choices": [
        {
          "id": "yes",
          "meaning": "Audio works after unmuting.",
          "targetNodeId": "success"
        },
        {
          "id": "no",
          "meaning": "Audio remains unavailable.",
          "targetNodeId": "audio-enhancements"
        }
      ]
    }
  ]
}
```

### 4.4 Runtime behavior

The AI may:

- Infer known context from earlier messages.
- Personalize wording.
- Explain why a step is relevant.
- Interpret free-form replies as graph choices.
- Avoid asking questions whose answers are already known.
- Recognize previously completed steps.
- Ask clarification when confidence is low.
- Suggest switching solutions when symptoms do not fit.
- Summarize completed diagnostics during escalation.

The AI may not:

- Invent unsupported remediation.
- Transition to arbitrary node IDs.
- Skip safety-critical steps.
- Treat an ambiguous reply as confirmation.
- Modify links, commands, or warnings.
- Claim success without user confirmation.

### 4.5 Choice interpretation

Example user response:

> I updated Windows yesterday and the speakers are still silent.

The skill can interpret this as:

```json
{
  "stepId": "windows-update",
  "choiceId": "no",
  "confidence": 0.97,
  "evidence": "User completed Windows Update and reports no sound."
}
```

The backend verifies that:

- The session is currently at `windows-update`.
- `no` is a valid choice.
- The target node exists.
- The solution version has not changed.

If confidence is below a defined threshold, the agent asks for clarification instead of submitting a choice.

### 4.6 Recommended confidence policy

| Confidence | Behavior |
|---|---|
| >= 0.90 | Submit the inferred choice |
| 0.65-0.89 | Confirm: "It sounds like that did not solve it - is that correct?" |
| < 0.65 | Ask the original structured question |

Safety-sensitive choices should always require explicit confirmation.

### 4.7 Context model

```json
{
  "device": {
    "operatingSystem": "Windows 11",
    "connectionType": "Bluetooth",
    "deviceType": "headphones"
  },
  "observations": [
    "Device appears in Bluetooth settings",
    "No audio is produced"
  ],
  "completedActions": [
    "Installed Windows updates",
    "Checked mute control"
  ],
  "currentNodeId": "default-device",
  "discardedHypotheses": [
    "System muted"
  ]
}
```

### 4.8 Advantages

- Feels like an intelligent support engineer.
- Understands natural and incomplete replies.
- Avoids redundant questions.
- Can explain technical steps at the user's level.
- Uses information from the whole conversation.
- Produces better escalation summaries.
- Can select between related Alchemy solutions.

### 4.9 Limitations

- More complex to implement and evaluate.
- Higher token and latency cost.
- Greater risk of unintended interpretation.
- Requires confidence thresholds and transition validation.
- Full graphs may exceed practical context limits.
- Behavior can vary between model versions.

## 5. Comparison

| Dimension | Deterministic Stepper | AI Troubleshooting Skill |
|---|---|---|
| Conversation quality | Structured and mechanical | Natural and adaptive |
| Graph navigation | Entirely server-controlled | AI proposes; server validates |
| Reliability | Very high | High with constraints |
| Hallucination risk | Very low | Moderate without guardrails |
| Natural-language understanding | Basic | Strong |
| Redundant questions | More likely | Can avoid them |
| Explainability | Current node only | Can explain diagnostic reasoning |
| Implementation complexity | Low-medium | Medium-high |
| Testing complexity | Straightforward path tests | Path, prompt, inference, and model tests |
| Token usage | Low | Higher |
| Latency | Lower | Higher |
| Auditability | Excellent | Good if decisions are logged |
| Personalization | Limited | Strong |
| Solution switching | Service-driven | AI-assisted |
| Best use | MVP and regulated workflows | Premium support experience |

## 6. Recommended Design: Constrained Hybrid

The approaches should not be mutually exclusive. Use Idea A as the execution foundation and Idea B as the conversational layer.

```text
AI controls:
- intent understanding
- explanations
- conversational wording
- context extraction
- mapping user replies to choices
- recommending a solution switch

Alchemy controls:
- approved instructions
- valid choices
- state transitions
- safety restrictions
- solution versioning
- success and escalation outcomes
```

The AI should never directly set `currentNodeId`. It submits a valid `choiceId`; Alchemy computes the next node.

### Execution modes

The skill can dynamically use two modes:

- **Guided mode:** AI interprets and adapts the experience.
- **Strict mode:** Exact deterministic presentation for sensitive, ambiguous, or high-risk steps.

## 7. Microsoft 365 Copilot Integration

Use:

- A **declarative agent** for the troubleshooting persona and instructions.
- An **API plugin** described through OpenAPI.
- **Adaptive Cards** for structured steps and explicit choices.
- Microsoft Entra ID for user authentication.
- A backend session service for graph traversal and validation.

Users would access the published troubleshooting agent from Microsoft 365 Copilot Chat or Teams.

## 8. Security and Governance

- Authenticate every request with Entra ID.
- Authorize solutions based on tenant, user, device, and support area.
- Do not place secrets or sensitive device data in prompts.
- Log graph decisions separately from model-generated explanations.
- Version each solution and preserve the version for active sessions.
- Sanitize Alchemy HTML and links before rendering.
- Require explicit confirmation for destructive or privileged operations.
- Apply retention policies to transcripts and diagnostic information.

## 9. Telemetry

Capture:

```json
{
  "sessionId": "session-123",
  "solutionId": "audio-windows-11",
  "solutionVersion": "1.5",
  "nodeId": "unmute",
  "choiceId": "no",
  "choiceSource": "ai-inferred",
  "confidence": 0.96,
  "latencyMs": 430,
  "outcome": "continued"
}
```

Important metrics include:

- Resolution and escalation rates.
- Time and turns to resolution.
- Abandonment by node.
- AI choice-confirmation rate.
- Incorrect inference rate.
- Solution-switch rate.
- User satisfaction.
- Most successful remediation steps.

## 10. Testing Strategy

### Deterministic tests

- Every choice points to an existing node.
- Every graph has a valid start and terminal path.
- Cycles are intentional and bounded.
- Resources resolve successfully.
- Invalid or stale choices are rejected.
- Sessions resume at the correct node.

### AI behavior tests

- Free-form answers map to the correct choices.
- Ambiguous answers trigger clarification.
- Completed steps are not unnecessarily repeated.
- Safety instructions remain unchanged.
- The model never invents node IDs or choices.
- Prompt injection cannot bypass the graph.
- The agent correctly recognizes out-of-scope symptoms.

### End-to-end scenarios

Test canonical paths including:

- Immediate resolution.
- Full unsuccessful path.
- Bluetooth redirect.
- Driver update and reinstall.
- Surface escalation.
- Session interruption and resumption.
- Mid-session solution version change.

## 11. Delivery Plan

1. **Phase 1:** Implement the deterministic session API and declarative agent.
2. **Phase 2:** Add natural-language choice interpretation with confirmation.
3. **Phase 3:** Add complete-solution understanding, context extraction, and redundant-step avoidance.
4. **Phase 4:** Add solution switching, escalation summaries, experimentation, and optimization.

This phased hybrid approach delivers a dependable MVP while preserving a path toward an AI-native troubleshooting experience.

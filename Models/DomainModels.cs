using System.Text.Json;

namespace AlchemyProxy.Models;

public sealed class SolutionSnapshot
{
    public required string SolutionId { get; init; }

    public required string Version { get; set; }

    public required string Locale { get; init; }

    public required string Title { get; init; }

    public required string RootGraphId { get; init; }

    public required DateTimeOffset LoadedAt { get; init; }

    public required Dictionary<string, SolutionGraph> Graphs { get; init; }
}

public sealed class SolutionGraph
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string StartNodeId { get; init; }

    public required Dictionary<string, SolutionNode> Nodes { get; init; }
}

public sealed class SolutionNode
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Name { get; init; }

    public ContentBlock? Instruction { get; init; }

    public ContentBlock? Question { get; init; }

    public IReadOnlyList<SolutionChoice> Choices { get; init; } = [];

    public string? Outcome { get; init; }

    public string? TargetGraphId { get; init; }
}

public sealed class ContentBlock
{
    public string? PlainText { get; init; }

    public string? ResourceId { get; init; }
}

public sealed class SolutionChoice
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public string? ResourceId { get; init; }

    public required string TargetNodeId { get; init; }
}

public sealed class TroubleshootingSession
{
    public required string SessionId { get; init; }

    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required string Query { get; init; }

    public required string Locale { get; init; }

    public required string SolutionId { get; init; }

    public required string SolutionVersion { get; init; }

    public required string CurrentGraphId { get; set; }

    public required string CurrentNodeId { get; set; }

    public required string Status { get; set; }

    public required int Revision { get; set; }

    public required string ContextJson { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class SessionEvent
{
    public required string SessionId { get; init; }

    public required int Sequence { get; init; }

    public required string EventType { get; init; }

    public string? GraphId { get; init; }

    public string? NodeId { get; init; }

    public string? ChoiceId { get; init; }

    public string? ChoiceSource { get; init; }

    public double? Confidence { get; init; }

    public string? Evidence { get; init; }

    public string? UserMessage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SessionOwner(string TenantId, string UserId);

public sealed record StartSessionRequest(
    string Query,
    string? Locale,
    string? Client,
    JsonElement? Context);

public sealed record AnswerStepReference(string GraphId, string NodeId);

public sealed record AnswerSessionRequest(
    AnswerStepReference Step,
    string ChoiceId,
    int ExpectedRevision,
    string? ChoiceSource,
    double? Confidence,
    string? Evidence,
    string? UserMessage);

public sealed record EscalateSessionRequest(
    int ExpectedRevision,
    string? Reason,
    string? UserMessage);

public sealed class SessionResponse
{
    public required string SessionId { get; init; }

    public required string Status { get; init; }

    public required int Revision { get; init; }

    public SolutionSummary? Solution { get; init; }

    public StepResponse? Step { get; init; }
}

public sealed record SolutionSummary(string Id, string Version, string Title);

public sealed class StepResponse
{
    public required string GraphId { get; init; }

    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Name { get; init; }

    public ContentBlock? Instruction { get; init; }

    public string? Question { get; init; }

    public IReadOnlyList<StepChoiceResponse> Choices { get; init; } = [];

    public string? Outcome { get; init; }

    public IReadOnlyList<StepActionResponse> AvailableActions { get; init; } = [];
}

public sealed record StepChoiceResponse(string Id, string Label);

public sealed record StepActionResponse(string Type, string Label);

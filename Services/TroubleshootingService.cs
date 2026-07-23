using System.Text.Json;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;
using AlchemyProxy.Storage;
using Microsoft.Extensions.Options;

namespace AlchemyProxy.Services;

public sealed class TroubleshootingService(
    SolutionLoader solutionLoader,
    FileSolutionSnapshotStore snapshotStore,
    SqliteSessionStore sessionStore,
    IOptions<LocalStorageOptions> storageOptions)
{
    public async Task<SessionResponse> StartAsync(
        StartSessionRequest request,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2_000)
        {
            throw new ApiException(400, "invalid_request", "Query must contain between 1 and 2,000 characters.");
        }

        var locale = NormalizeLocale(request.Locale);
        var snapshot = await solutionLoader.LoadRootAsync(request.Query.Trim(), locale, cancellationToken);
        var position = await ResolvePositionAsync(
            snapshot,
            snapshot.RootGraphId,
            snapshot.Graphs[snapshot.RootGraphId].StartNodeId,
            cancellationToken);
        await snapshotStore.SaveAsync(snapshot, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new TroubleshootingSession
        {
            SessionId = Guid.CreateVersion7().ToString("N"),
            TenantId = owner.TenantId,
            UserId = owner.UserId,
            Query = request.Query.Trim(),
            Locale = locale,
            SolutionId = snapshot.SolutionId,
            SolutionVersion = snapshot.Version,
            CurrentGraphId = position.GraphId,
            CurrentNodeId = position.Node.Id,
            Status = GetStatus(position.Node),
            Revision = 1,
            ContextJson = request.Context?.GetRawText() ?? "{}",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(storageOptions.Value.SessionTtlHours)
        };

        await sessionStore.CreateAsync(
            session,
            new SessionEvent
            {
                SessionId = session.SessionId,
                Sequence = 1,
                EventType = "sessionStarted",
                GraphId = session.CurrentGraphId,
                NodeId = session.CurrentNodeId,
                ChoiceSource = "system",
                CreatedAt = now
            },
            cancellationToken);

        return await ToResponseAsync(session, snapshot, includeSolution: true, cancellationToken);
    }

    public async Task<SessionResponse> GetAsync(
        string sessionId,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(sessionId, owner, cancellationToken);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        return await ToResponseAsync(session, snapshot, includeSolution: true, cancellationToken);
    }

    public async Task<SessionResponse> AnswerAsync(
        string sessionId,
        AnswerSessionRequest request,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(sessionId, owner, cancellationToken);
        EnsureActive(session);

        if (request.ExpectedRevision != session.Revision)
        {
            throw new ApiException(409, "stale_session_revision", "The troubleshooting session has changed.");
        }

        if (request.Confidence is < 0 or > 1)
        {
            throw new ApiException(400, "invalid_request", "Confidence must be between 0 and 1.");
        }

        if (!string.Equals(request.Step.GraphId, session.CurrentGraphId, StringComparison.Ordinal) ||
            !string.Equals(request.Step.NodeId, session.CurrentNodeId, StringComparison.Ordinal))
        {
            throw new ApiException(409, "step_mismatch", "The submitted step is not the current session step.");
        }

        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        var currentNode = snapshot.Graphs[session.CurrentGraphId].Nodes[session.CurrentNodeId];
        if (currentNode.Type != "choice")
        {
            throw new ApiException(409, "session_not_active", "The current step does not accept an answer.");
        }

        var choice = currentNode.Choices.SingleOrDefault(
            candidate => string.Equals(candidate.Id, request.ChoiceId, StringComparison.Ordinal));
        if (choice is null)
        {
            throw new ApiException(422, "invalid_choice", "The choice is not valid for the current step.");
        }

        var previousRevision = session.Revision;
        var position = await ResolvePositionAsync(
            snapshot,
            session.CurrentGraphId,
            choice.TargetNodeId,
            cancellationToken);
        if (position.SnapshotChanged)
        {
            await snapshotStore.UpdateAsync(snapshot, cancellationToken);
        }
        var now = DateTimeOffset.UtcNow;
        session.CurrentGraphId = position.GraphId;
        session.CurrentNodeId = position.Node.Id;
        session.Status = GetStatus(position.Node);
        session.Revision++;
        session.UpdatedAt = now;

        await sessionStore.UpdateAsync(
            session,
            previousRevision,
            new SessionEvent
            {
                SessionId = session.SessionId,
                Sequence = session.Revision,
                EventType = "answerSubmitted",
                GraphId = request.Step.GraphId,
                NodeId = request.Step.NodeId,
                ChoiceId = request.ChoiceId,
                ChoiceSource = request.ChoiceSource ?? "explicit",
                Confidence = request.Confidence,
                Evidence = request.Evidence,
                UserMessage = request.UserMessage,
                CreatedAt = now
            },
            cancellationToken);

        return await ToResponseAsync(session, snapshot, includeSolution: false, cancellationToken);
    }

    public async Task<SessionResponse> EscalateAsync(
        string sessionId,
        EscalateSessionRequest request,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(sessionId, owner, cancellationToken);
        if (request.ExpectedRevision != session.Revision)
        {
            throw new ApiException(409, "stale_session_revision", "The troubleshooting session has changed.");
        }

        if (session.Status is "resolved" or "escalated" or "expired")
        {
            throw new ApiException(409, "session_not_active", "The session cannot be escalated.");
        }

        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        var previousRevision = session.Revision;
        var now = DateTimeOffset.UtcNow;
        session.Status = "escalated";
        session.Revision++;
        session.UpdatedAt = now;

        await sessionStore.UpdateAsync(
            session,
            previousRevision,
            new SessionEvent
            {
                SessionId = session.SessionId,
                Sequence = session.Revision,
                EventType = "sessionEscalated",
                GraphId = session.CurrentGraphId,
                NodeId = session.CurrentNodeId,
                ChoiceSource = "explicit",
                Evidence = request.Reason,
                UserMessage = request.UserMessage,
                CreatedAt = now
            },
            cancellationToken);

        return await ToResponseAsync(session, snapshot, includeSolution: false, cancellationToken);
    }

    public async Task<object> GetContextAsync(
        string sessionId,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(sessionId, owner, cancellationToken);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        if (await solutionLoader.ExpandAllAsync(snapshot, cancellationToken))
        {
            await snapshotStore.UpdateAsync(snapshot, cancellationToken);
        }

        var hydratedSnapshot = await solutionLoader.HydrateSnapshotAsync(snapshot, cancellationToken);
        var events = await sessionStore.GetEventsAsync(sessionId, cancellationToken);

        return new
        {
            session.SessionId,
            session.SolutionId,
            session.SolutionVersion,
            session.CurrentGraphId,
            session.CurrentNodeId,
            Constraints = new[]
            {
                "Use only listed choices for transitions.",
                "Do not invent troubleshooting instructions.",
                "Do not claim success without user confirmation."
            },
            Snapshot = hydratedSnapshot,
            History = events
        };
    }

    private async Task<TroubleshootingSession> GetOwnedSessionAsync(
        string sessionId,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetAsync(sessionId, cancellationToken)
            ?? throw new ApiException(404, "session_not_found", "The troubleshooting session was not found.");

        if (!string.Equals(session.TenantId, owner.TenantId, StringComparison.Ordinal) ||
            !string.Equals(session.UserId, owner.UserId, StringComparison.Ordinal))
        {
            throw new ApiException(403, "session_access_denied", "The session belongs to another user.");
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ApiException(409, "session_not_active", "The troubleshooting session has expired.");
        }

        return session;
    }

    private Task<SolutionSnapshot> LoadSnapshotAsync(
        TroubleshootingSession session,
        CancellationToken cancellationToken) =>
        snapshotStore.LoadAsync(
            session.SolutionId,
            session.SolutionVersion,
            session.Locale,
            cancellationToken);

    private async Task<ResolvedPosition> ResolvePositionAsync(
        SolutionSnapshot snapshot,
        string graphId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var redirects = new HashSet<string>(StringComparer.Ordinal);
        var snapshotChanged = false;
        while (true)
        {
            if (!snapshot.Graphs.TryGetValue(graphId, out var graph) ||
                !graph.Nodes.TryGetValue(nodeId, out var node))
            {
                throw new ApiException(500, "solution_snapshot_invalid", "The session points to a missing solution node.");
            }

            if (node.Type != "redirect")
            {
                return new ResolvedPosition(graphId, node, snapshotChanged);
            }

            if (!redirects.Add($"{graphId}:{nodeId}") || node.TargetGraphId is null)
            {
                throw new ApiException(500, "solution_snapshot_invalid", "The solution contains an invalid redirect.");
            }

            if (!snapshot.Graphs.TryGetValue(node.TargetGraphId, out var targetGraph))
            {
                targetGraph = await solutionLoader.LoadBranchAsync(
                    node.TargetGraphId,
                    snapshot.Locale,
                    cancellationToken);
                snapshot.Graphs.Add(targetGraph.Id, targetGraph);
                snapshotChanged = true;
            }

            graphId = targetGraph.Id;
            nodeId = targetGraph.StartNodeId;
        }
    }

    private static string GetStatus(SolutionNode node)
    {
        if (node.Type != "text")
        {
            return "inProgress";
        }

        return node.Outcome switch
        {
            "successful" => "resolved",
            "unsuccessful" => "unresolved",
            _ => "unresolved"
        };
    }

    private static void EnsureActive(TroubleshootingSession session)
    {
        if (session.Status != "inProgress")
        {
            throw new ApiException(409, "session_not_active", "The troubleshooting session is not active.");
        }
    }

    private async Task<SessionResponse> ToResponseAsync(
        TroubleshootingSession session,
        SolutionSnapshot snapshot,
        bool includeSolution,
        CancellationToken cancellationToken)
    {
        var storedNode = snapshot.Graphs[session.CurrentGraphId].Nodes[session.CurrentNodeId];
        var node = await solutionLoader.HydrateNodeAsync(
            storedNode,
            session.Locale,
            cancellationToken);
        return new SessionResponse
        {
            SessionId = session.SessionId,
            Status = session.Status,
            Revision = session.Revision,
            Solution = includeSolution
                ? new SolutionSummary(snapshot.SolutionId, snapshot.Version, snapshot.Title)
                : null,
            Step = new StepResponse
            {
                GraphId = session.CurrentGraphId,
                Id = node.Id,
                Type = node.Type == "text" ? "terminal" : node.Type,
                Name = node.Name,
                Instruction = node.Instruction,
                Question = node.Question?.PlainText,
                Choices = node.Choices.Select(choice => new StepChoiceResponse(
                    choice.Id,
                    choice.Label ?? throw new ApiException(
                        500,
                        "solution_snapshot_invalid",
                        $"Choice {choice.Id} has no display label."))).ToArray(),
                Outcome = node.Outcome,
                AvailableActions = session.Status == "unresolved"
                    ? [new StepActionResponse("escalate", "Contact support")]
                    : []
            }
        };
    }

    private static string NormalizeLocale(string? locale)
    {
        var normalized = string.IsNullOrWhiteSpace(locale) ? "en-us" : locale.Trim().ToLowerInvariant();
        if (normalized.Length > 20 ||
            normalized.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new ApiException(400, "invalid_request", "Locale is invalid.");
        }

        return normalized;
    }

    private sealed record ResolvedPosition(
        string GraphId,
        SolutionNode Node,
        bool SnapshotChanged);
}

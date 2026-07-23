using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;
using AlchemyProxy.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace AlchemyProxy.Services;

public sealed partial class SolutionLoader(
    AlchemyClient alchemyClient,
    FileSolutionSnapshotStore snapshotStore,
    IMemoryCache memoryCache)
{
    private const int MaxGraphs = 100;
    private const int MaxNodes = 5_000;
    private const int MaxResources = 2_000;

    public async Task<SolutionSnapshot> LoadRootAsync(
        string query,
        string locale,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"root:{locale}:{query.Trim().ToUpperInvariant()}";
        if (!memoryCache.TryGetValue(cacheKey, out SolutionGraph? rootGraph) || rootGraph is null)
        {
            var rootJson = await alchemyClient.SearchBranchingGraphAsync(query, cancellationToken);
            rootGraph = NormalizeGraph(ParseGraph(rootJson));
            ValidateGraph(rootGraph);
            memoryCache.Set(cacheKey, rootGraph, TimeSpan.FromMinutes(15));
        }

        var snapshot = new SolutionSnapshot
        {
            SolutionId = rootGraph.Id,
            Version = ComputeVersion(rootGraph),
            Locale = locale,
            Title = rootGraph.Title,
            RootGraphId = rootGraph.Id,
            LoadedAt = DateTimeOffset.UtcNow,
            Graphs = new Dictionary<string, SolutionGraph>(StringComparer.Ordinal)
            {
                [rootGraph.Id] = rootGraph
            }
        };

        return snapshot;
    }

    public async Task<SolutionGraph> LoadBranchAsync(
        string branchId,
        string locale,
        CancellationToken cancellationToken)
    {
        var cached = await snapshotStore.TryLoadGraphAsync(branchId, locale, cancellationToken);
        if (cached is not null)
        {
            ValidateGraph(cached);
            return cached;
        }

        var branchJson = await alchemyClient.GetBranchGraphAsync(branchId, locale, cancellationToken);
        var graph = NormalizeGraph(ParseGraph(branchJson));
        if (!string.Equals(branchId, graph.Id, StringComparison.Ordinal))
        {
            throw InvalidGraph($"Redirect target {branchId} returned graph {graph.Id}.");
        }

        ValidateGraph(graph);
        await snapshotStore.SaveGraphAsync(graph, locale, cancellationToken);
        return graph;
    }

    public async Task<bool> ExpandAllAsync(
        SolutionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        foreach (var graph in snapshot.Graphs.Values)
        {
            EnqueueRedirects(graph, queue);
        }

        var changed = false;
        while (queue.Count > 0)
        {
            if (snapshot.Graphs.Count >= MaxGraphs)
            {
                throw InvalidGraph("The solution exceeds the maximum graph count.");
            }

            var branchId = queue.Dequeue();
            if (snapshot.Graphs.ContainsKey(branchId))
            {
                continue;
            }

            var graph = await LoadBranchAsync(branchId, snapshot.Locale, cancellationToken);
            snapshot.Graphs.Add(graph.Id, graph);
            EnqueueRedirects(graph, queue);
            changed = true;
        }

        if (snapshot.Graphs.Values.Sum(graph => graph.Nodes.Count) > MaxNodes)
        {
            throw InvalidGraph("The solution exceeds the maximum node count.");
        }

        return changed;
    }

    public async Task<SolutionNode> HydrateNodeAsync(
        SolutionNode node,
        string locale,
        CancellationToken cancellationToken)
    {
        var instruction = await HydrateContentAsync(node.Instruction, locale, cancellationToken);
        var question = await HydrateContentAsync(node.Question, locale, cancellationToken);
        var choices = new List<SolutionChoice>(node.Choices.Count);

        foreach (var choice in node.Choices)
        {
            var label = choice.Label;
            if (label is null && choice.ResourceId is not null)
            {
                label = await GetResourcePlainTextAsync(choice.ResourceId, locale, cancellationToken);
            }

            choices.Add(new SolutionChoice
            {
                Id = choice.Id,
                Label = label,
                ResourceId = choice.ResourceId,
                TargetNodeId = choice.TargetNodeId
            });
        }

        return new SolutionNode
        {
            Id = node.Id,
            Type = node.Type,
            Name = node.Name,
            Instruction = instruction,
            Question = question,
            Choices = choices,
            Outcome = node.Outcome,
            TargetGraphId = node.TargetGraphId
        };
    }

    public async Task<SolutionSnapshot> HydrateSnapshotAsync(
        SolutionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in snapshot.Graphs.Values.SelectMany(graph => graph.Nodes.Values))
        {
            AddResourceId(node.Instruction, resourceIds);
            AddResourceId(node.Question, resourceIds);
            foreach (var choice in node.Choices)
            {
                if (choice.ResourceId is not null)
                {
                    resourceIds.Add(choice.ResourceId);
                }
            }
        }

        if (resourceIds.Count > MaxResources)
        {
            throw InvalidGraph("The solution exceeds the maximum resource count.");
        }

        var resourceTasks = resourceIds.ToDictionary(
            resourceId => resourceId,
            resourceId => GetResourcePlainTextAsync(resourceId, snapshot.Locale, cancellationToken),
            StringComparer.Ordinal);
        await Task.WhenAll(resourceTasks.Values);

        var resourceText = resourceTasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Result,
            StringComparer.Ordinal);

        var graphs = snapshot.Graphs.ToDictionary(
            pair => pair.Key,
            pair => new SolutionGraph
            {
                Id = pair.Value.Id,
                Title = pair.Value.Title,
                StartNodeId = pair.Value.StartNodeId,
                Nodes = pair.Value.Nodes.ToDictionary(
                    nodePair => nodePair.Key,
                    nodePair => HydrateNodeFromCache(nodePair.Value, resourceText),
                    StringComparer.Ordinal)
            },
            StringComparer.Ordinal);

        return new SolutionSnapshot
        {
            SolutionId = snapshot.SolutionId,
            Version = snapshot.Version,
            Locale = snapshot.Locale,
            Title = snapshot.Title,
            RootGraphId = snapshot.RootGraphId,
            LoadedAt = snapshot.LoadedAt,
            Graphs = graphs
        };
    }

    private async Task<ContentBlock?> HydrateContentAsync(
        ContentBlock? content,
        string locale,
        CancellationToken cancellationToken)
    {
        if (content?.ResourceId is null || content.PlainText is not null)
        {
            return content;
        }

        return new ContentBlock
        {
            ResourceId = content.ResourceId,
            PlainText = await GetResourcePlainTextAsync(content.ResourceId, locale, cancellationToken)
        };
    }

    private async Task<string> GetResourcePlainTextAsync(
        string resourceId,
        string locale,
        CancellationToken cancellationToken)
    {
        var cached = await snapshotStore.TryLoadResourceAsync(resourceId, locale, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var resource = await alchemyClient.GetResourceAsync(resourceId, locale, cancellationToken);
        var plainText = HtmlToPlainText(resource.Html);
        await snapshotStore.SaveResourceAsync(resourceId, locale, plainText, cancellationToken);
        return plainText;
    }

    private static JsonElement ParseGraph(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SolutionGraph NormalizeGraph(JsonElement graph)
    {
        var id = GetGraphId(graph);
        var title = graph.GetProperty("meta").TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? id
            : id;
        var startNodeId = GetRequiredString(graph, "startNodeId");
        var nodes = new Dictionary<string, SolutionNode>(StringComparer.Ordinal);

        if (!graph.TryGetProperty("nodes", out var rawNodes) ||
            rawNodes.ValueKind != JsonValueKind.Array)
        {
            throw InvalidGraph($"Graph {id} is missing its nodes array.");
        }

        foreach (var rawNode in rawNodes.EnumerateArray())
        {
            var node = NormalizeNode(rawNode);
            if (!nodes.TryAdd(node.Id, node))
            {
                throw InvalidGraph($"Graph {id} contains duplicate node ID {node.Id}.");
            }
        }

        return new SolutionGraph
        {
            Id = id,
            Title = title,
            StartNodeId = startNodeId,
            Nodes = nodes
        };
    }

    private static SolutionNode NormalizeNode(JsonElement node)
    {
        var sourceType = GetRequiredString(node, "type");
        var type = sourceType.ToLowerInvariant() switch
        {
            "choicenode" => "choice",
            "textnode" => "text",
            "redirectnode" => "redirect",
            _ => throw InvalidGraph($"Unsupported node type {sourceType}.")
        };

        var choices = new List<SolutionChoice>();
        if (node.TryGetProperty("staticChoices", out var rawChoices) &&
            rawChoices.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawChoice in rawChoices.EnumerateArray())
            {
                if (rawChoice.ValueKind != JsonValueKind.Object ||
                    !rawChoice.TryGetProperty("id", out _))
                {
                    continue;
                }

                var label = ResolveContent(rawChoice, "text");
                choices.Add(new SolutionChoice
                {
                    Id = GetRequiredString(rawChoice, "id"),
                    Label = label?.PlainText,
                    ResourceId = label?.ResourceId,
                    TargetNodeId = GetRequiredString(rawChoice, "targetNodeId")
                });
            }
        }

        return new SolutionNode
        {
            Id = GetRequiredString(node, "id"),
            Type = type,
            Name = GetOptionalString(node, "name"),
            Instruction = ResolveContent(node, "text"),
            Question = ResolveContent(node, "question"),
            Choices = choices,
            Outcome = GetOptionalString(node, "outcomeSignal")?.ToLowerInvariant(),
            TargetGraphId = GetOptionalString(node, "targetDialogId")
        };
    }

    private static ContentBlock? ResolveContent(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var content) ||
            content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var contentType = GetOptionalString(content, "contentType");
        var value = GetOptionalString(content, "content");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return contentType?.ToLowerInvariant() switch
        {
            "embeddedplaintext" => new ContentBlock { PlainText = value },
            "alchemyresource" => new ContentBlock { ResourceId = value },
            _ => throw InvalidGraph($"Unsupported content type {contentType}.")
        };
    }

    private static void ValidateGraph(SolutionGraph graph)
    {
        if (!graph.Nodes.ContainsKey(graph.StartNodeId))
        {
            throw InvalidGraph($"Graph {graph.Id} has an invalid start node.");
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (node.Type == "choice")
            {
                if (node.Choices.Count == 0)
                {
                    throw InvalidGraph($"Choice node {node.Id} has no choices.");
                }

                foreach (var choice in node.Choices)
                {
                    if (!graph.Nodes.ContainsKey(choice.TargetNodeId))
                    {
                        throw InvalidGraph($"Choice {choice.Id} targets missing node {choice.TargetNodeId}.");
                    }
                }
            }

            if (node.Type == "redirect" && string.IsNullOrWhiteSpace(node.TargetGraphId))
            {
                throw InvalidGraph($"Redirect node {node.Id} has no target graph.");
            }
        }
    }

    private static string ComputeVersion(SolutionGraph rootGraph)
    {
        var json = JsonSerializer.Serialize(rootGraph);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void EnqueueRedirects(SolutionGraph graph, Queue<string> queue)
    {
        foreach (var target in graph.Nodes.Values
                     .Where(node => node.Type == "redirect" && node.TargetGraphId is not null)
                     .Select(node => node.TargetGraphId!))
        {
            queue.Enqueue(target);
        }
    }

    private static void AddResourceId(ContentBlock? content, HashSet<string> resourceIds)
    {
        if (content?.ResourceId is not null)
        {
            resourceIds.Add(content.ResourceId);
        }
    }

    private static SolutionNode HydrateNodeFromCache(
        SolutionNode node,
        IReadOnlyDictionary<string, string> resourceText) =>
        new()
        {
            Id = node.Id,
            Type = node.Type,
            Name = node.Name,
            Instruction = HydrateContentFromCache(node.Instruction, resourceText),
            Question = HydrateContentFromCache(node.Question, resourceText),
            Choices = node.Choices.Select(choice => new SolutionChoice
            {
                Id = choice.Id,
                Label = choice.Label ??
                    (choice.ResourceId is not null ? resourceText[choice.ResourceId] : null),
                ResourceId = choice.ResourceId,
                TargetNodeId = choice.TargetNodeId
            }).ToArray(),
            Outcome = node.Outcome,
            TargetGraphId = node.TargetGraphId
        };

    private static ContentBlock? HydrateContentFromCache(
        ContentBlock? content,
        IReadOnlyDictionary<string, string> resourceText) =>
        content?.ResourceId is null
            ? content
            : new ContentBlock
            {
                ResourceId = content.ResourceId,
                PlainText = resourceText[content.ResourceId]
            };

    private static string GetGraphId(JsonElement graph) =>
        graph.TryGetProperty("meta", out var meta)
            ? GetRequiredString(meta, "id")
            : throw InvalidGraph("A graph is missing metadata.");

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw InvalidGraph($"Required property {propertyName} is missing.");

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ApiException InvalidGraph(string message) =>
        new(422, "invalid_solution_graph", message);

    private static string HtmlToPlainText(string html)
    {
        var withLines = BlockBreakRegex().Replace(html, "\n");
        var withoutTags = HtmlTagRegex().Replace(withLines, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalizedLines = decoded
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => InlineWhitespaceRegex().Replace(line, " "));
        return string.Join('\n', normalizedLines);
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>|<\s*/\s*(div|p|li|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex InlineWhitespaceRegex();
}

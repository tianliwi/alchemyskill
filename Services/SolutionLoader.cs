using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;

namespace AlchemyProxy.Services;

public sealed partial class SolutionLoader(AlchemyClient alchemyClient)
{
    private const int MaxGraphs = 100;
    private const int MaxNodes = 5_000;
    private const int MaxResources = 2_000;

    public async Task<SolutionSnapshot> LoadAsync(
        string query,
        string locale,
        CancellationToken cancellationToken)
    {
        var rootJson = await alchemyClient.SearchBranchingGraphAsync(query, cancellationToken);
        var rawGraphs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        var root = ParseGraph(rootJson);
        var rootId = GetGraphId(root);
        rawGraphs.Add(rootId, root);
        EnqueueRedirects(root, queue);

        while (queue.Count > 0)
        {
            if (rawGraphs.Count >= MaxGraphs)
            {
                throw InvalidGraph("The solution exceeds the maximum graph count.");
            }

            var branchId = queue.Dequeue();
            if (rawGraphs.ContainsKey(branchId))
            {
                continue;
            }

            var branchJson = await alchemyClient.GetBranchGraphAsync(branchId, locale, cancellationToken);
            var branch = ParseGraph(branchJson);
            var parsedId = GetGraphId(branch);
            if (!string.Equals(branchId, parsedId, StringComparison.Ordinal))
            {
                throw InvalidGraph($"Redirect target {branchId} returned graph {parsedId}.");
            }

            rawGraphs.Add(parsedId, branch);
            EnqueueRedirects(branch, queue);
        }

        var totalNodes = rawGraphs.Values.Sum(GetNodes);
        if (totalNodes > MaxNodes)
        {
            throw InvalidGraph("The solution exceeds the maximum node count.");
        }

        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var graph in rawGraphs.Values)
        {
            CollectResourceIds(graph, resourceIds);
        }

        if (resourceIds.Count > MaxResources)
        {
            throw InvalidGraph("The solution exceeds the maximum resource count.");
        }

        var resources = new Dictionary<string, AlchemyResource>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            resourceIds,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (resourceId, token) =>
            {
                var resource = await alchemyClient.GetResourceAsync(resourceId, locale, token);
                lock (resources)
                {
                    resources.Add(resourceId, resource);
                }
            });

        var graphs = rawGraphs.ToDictionary(
            pair => pair.Key,
            pair => NormalizeGraph(pair.Value, resources),
            StringComparer.Ordinal);

        ValidateGraphs(rootId, graphs);

        var rootGraph = graphs[rootId];
        var snapshot = new SolutionSnapshot
        {
            SolutionId = rootId,
            Version = string.Empty,
            Locale = locale,
            Title = rootGraph.Title,
            RootGraphId = rootId,
            LoadedAt = DateTimeOffset.UtcNow,
            Graphs = graphs
        };

        snapshot.Version = ComputeVersion(snapshot);
        return snapshot;
    }

    private static JsonElement ParseGraph(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SolutionGraph NormalizeGraph(
        JsonElement graph,
        IReadOnlyDictionary<string, AlchemyResource> resources)
    {
        var id = GetGraphId(graph);
        var title = graph.GetProperty("meta").TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? id
            : id;
        var startNodeId = GetRequiredString(graph, "startNodeId");
        var nodes = new Dictionary<string, SolutionNode>(StringComparer.Ordinal);

        foreach (var rawNode in graph.GetProperty("nodes").EnumerateArray())
        {
            var node = NormalizeNode(rawNode, resources);
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

    private static SolutionNode NormalizeNode(
        JsonElement node,
        IReadOnlyDictionary<string, AlchemyResource> resources)
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
                if (rawChoice.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                choices.Add(new SolutionChoice
                {
                    Id = GetRequiredString(rawChoice, "id"),
                    Label = ResolveContent(rawChoice, "text", resources)?.PlainText
                        ?? throw InvalidGraph("A choice is missing display text."),
                    TargetNodeId = GetRequiredString(rawChoice, "targetNodeId")
                });
            }
        }

        return new SolutionNode
        {
            Id = GetRequiredString(node, "id"),
            Type = type,
            Name = GetOptionalString(node, "name"),
            Instruction = ResolveContent(node, "text", resources),
            Question = ResolveContent(node, "question", resources)?.PlainText,
            Choices = choices,
            Outcome = GetOptionalString(node, "outcomeSignal")?.ToLowerInvariant(),
            TargetGraphId = GetOptionalString(node, "targetDialogId")
        };
    }

    private static ContentBlock? ResolveContent(
        JsonElement owner,
        string propertyName,
        IReadOnlyDictionary<string, AlchemyResource> resources)
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
            "alchemyresource" when resources.TryGetValue(value, out var resource) =>
                new ContentBlock { PlainText = HtmlToPlainText(resource.Html), ResourceId = value },
            "alchemyresource" => throw InvalidGraph($"Resource {value} was not resolved."),
            _ => throw InvalidGraph($"Unsupported content type {contentType}.")
        };
    }

    private static void ValidateGraphs(
        string rootGraphId,
        IReadOnlyDictionary<string, SolutionGraph> graphs)
    {
        if (!graphs.ContainsKey(rootGraphId))
        {
            throw InvalidGraph("The root graph is missing.");
        }

        foreach (var graph in graphs.Values)
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

                if (node.Type == "redirect" &&
                    (string.IsNullOrWhiteSpace(node.TargetGraphId) ||
                     !graphs.ContainsKey(node.TargetGraphId)))
                {
                    throw InvalidGraph($"Redirect node {node.Id} has an invalid target graph.");
                }
            }
        }
    }

    private static string ComputeVersion(SolutionSnapshot snapshot)
    {
        var loadedAt = snapshot.LoadedAt;
        var copy = new SolutionSnapshot
        {
            SolutionId = snapshot.SolutionId,
            Version = string.Empty,
            Locale = snapshot.Locale,
            Title = snapshot.Title,
            RootGraphId = snapshot.RootGraphId,
            LoadedAt = DateTimeOffset.UnixEpoch,
            Graphs = snapshot.Graphs
        };
        var json = JsonSerializer.Serialize(copy);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        _ = loadedAt;
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void EnqueueRedirects(JsonElement graph, Queue<string> queue)
    {
        foreach (var node in graph.GetProperty("nodes").EnumerateArray())
        {
            if (string.Equals(GetOptionalString(node, "type"), "redirectnode", StringComparison.OrdinalIgnoreCase) &&
                GetOptionalString(node, "targetDialogId") is { Length: > 0 } target)
            {
                queue.Enqueue(target);
            }
        }
    }

    private static void CollectResourceIds(JsonElement element, HashSet<string> resourceIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (string.Equals(
                    GetOptionalString(element, "contentType"),
                    "alchemyresource",
                    StringComparison.OrdinalIgnoreCase) &&
                GetOptionalString(element, "content") is { Length: > 0 } resourceId)
            {
                resourceIds.Add(resourceId);
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectResourceIds(property.Value, resourceIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectResourceIds(item, resourceIds);
            }
        }
    }

    private static int GetNodes(JsonElement graph) =>
        graph.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.GetArrayLength()
            : throw InvalidGraph("A graph is missing its nodes array.");

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

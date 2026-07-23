using System.Text;
using System.Text.Json;

namespace AlchemyProxy.Endpoints;

public static class LegacyAlchemyEndpoints
{
    public static IEndpointRouteBuilder MapLegacyAlchemyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/insights", async (
            InsightRequest request,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            using var response = await PostQueryAsync(request.Text, httpClientFactory, cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var insightText = document.RootElement.GetProperty("Insights")[0].GetProperty("Description").GetString();
            return insightText is null
                ? Results.Problem("No text found in first insight.")
                : Results.Json(JsonSerializer.Deserialize<JsonElement>(insightText));
        });

        endpoints.MapPost("/api/query", async (
            QueryRequest request,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            using var response = await PostQueryAsync(request.Query, httpClientFactory, cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var insightText = document.RootElement.GetProperty("Insights")[0].GetProperty("Description").GetString();
            if (insightText is null)
            {
                return Results.Problem("No text found in first insight.");
            }

            var insight = JsonSerializer.Deserialize<JsonElement>(insightText);
            var startNodeId = insight.GetProperty("startNodeId").GetString();
            var startNode = new Dictionary<string, object?>();
            var otherNodeIds = new List<string>();

            foreach (var node in insight.GetProperty("nodes").EnumerateArray())
            {
                var nodeId = node.GetProperty("id").GetString();
                if (nodeId == startNodeId)
                {
                    foreach (var property in node.EnumerateObject())
                    {
                        startNode[property.Name] = property.Value;
                    }
                }
                else if (nodeId is not null)
                {
                    otherNodeIds.Add(nodeId);
                }
            }

            startNode["otherNodeIds"] = otherNodeIds;
            return Results.Json(startNode);
        });

        endpoints.MapGet("/api/getresource/{id}", async (
            string id,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(
                $"https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/alchemyresource/{id}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var content = document.RootElement.GetProperty("SolutionContent").GetString();
            return content is null ? Results.Problem("No SolutionContent found.") : Results.Text(content, "text/html");
        });

        endpoints.MapGet("/api/getbranching/{id}/{nodeId?}", async (
            string id,
            string? nodeId,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(
                $"https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/branching/{id}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var content = document.RootElement.GetProperty("SolutionContent").GetString();
            if (content is null)
            {
                return Results.Problem("No SolutionContent found.");
            }

            var parsed = JsonSerializer.Deserialize<JsonElement>(content);
            if (!string.IsNullOrEmpty(nodeId) && parsed.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (node.GetProperty("id").GetString() == nodeId)
                    {
                        return Results.Json(node);
                    }
                }

                return Results.NotFound($"Node '{nodeId}' not found.");
            }

            return Results.Json(parsed);
        });

        return endpoints;
    }

    private static async Task<HttpResponseMessage> PostQueryAsync(
        string query,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new { Text = query });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            "https://alchemy.microsoft.com/api/v2/insights/app/servicenowvirtualagent",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed record InsightRequest(string Text);

    private sealed record QueryRequest(string Query);
}

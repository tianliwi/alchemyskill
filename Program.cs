using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

var app = builder.Build();

app.MapPost("/api/insights", async (InsightRequest request, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var payload = JsonSerializer.Serialize(new { Text = request.Text });
    var content = new StringContent(payload, Encoding.UTF8, "application/json");

    var response = await client.PostAsync(
        "https://alchemy.microsoft.com/api/v2/insights/app/servicenowvirtualagent", content);

    response.EnsureSuccessStatusCode();

    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var insightText = doc.RootElement
        .GetProperty("Insights")[0]
        .GetProperty("Description")
        .GetString();

    if (insightText is null)
        return Results.Problem("No text found in first insight.");

    // The text is escaped JSON — unescape and return as raw JSON
    var unescaped = JsonSerializer.Deserialize<JsonElement>(insightText);
    return Results.Json(unescaped);
});

app.MapPost("/api/getnode", async (GetNodeRequest request, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var payload = JsonSerializer.Serialize(new { Text = request.Query });
    var content = new StringContent(payload, Encoding.UTF8, "application/json");

    var response = await client.PostAsync(
        "https://alchemy.microsoft.com/api/v2/insights/app/servicenowvirtualagent", content);
    response.EnsureSuccessStatusCode();

    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var insightText = doc.RootElement
        .GetProperty("Insights")[0]
        .GetProperty("Description")
        .GetString();

    if (insightText is null)
        return Results.Problem("No text found in first insight.");

    var insight = JsonSerializer.Deserialize<JsonElement>(insightText);
    var startNodeId = insight.GetProperty("startNodeId").GetString();
    var nodes = insight.GetProperty("nodes");

    foreach (var node in nodes.EnumerateArray())
    {
        if (node.GetProperty("id").GetString() == startNodeId)
            return Results.Json(node);
    }

    return Results.NotFound("Start node not found.");
});

app.MapGet("/api/getresource/{id}", async (string id, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var response = await client.GetAsync(
        $"https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/alchemyresource/{id}");
    response.EnsureSuccessStatusCode();

    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var solutionContent = doc.RootElement.GetProperty("SolutionContent").GetString();

    if (solutionContent is null)
        return Results.Problem("No SolutionContent found.");

    return Results.Text(solutionContent, "application/json");
});

app.MapGet("/api/getbranching/{id}/{nodeId?}", async (string id, string? nodeId, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var response = await client.GetAsync(
        $"https://alchemy.microsoft.com/api/v2/app/servicenowvirtualagent/solutions/en-us/branching/{id}");
    response.EnsureSuccessStatusCode();

    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var solutionContent = doc.RootElement.GetProperty("SolutionContent").GetString();

    if (solutionContent is null)
        return Results.Problem("No SolutionContent found.");

    var parsed = JsonSerializer.Deserialize<JsonElement>(solutionContent);

    if (!string.IsNullOrEmpty(nodeId) && parsed.TryGetProperty("nodes", out var nodes))
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.GetProperty("id").GetString() == nodeId)
                return Results.Json(node);
        }
        return Results.NotFound($"Node '{nodeId}' not found.");
    }

    return Results.Json(parsed);
});

app.Run();

record InsightRequest(string Text);
record GetNodeRequest(string Query);
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

app.Run();

record InsightRequest(string Text);
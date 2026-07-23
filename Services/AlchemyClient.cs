using System.Net.Http.Json;
using System.Text.Json;
using AlchemyProxy.Infrastructure;
using Microsoft.Extensions.Options;

namespace AlchemyProxy.Services;

public sealed class AlchemyClient(HttpClient httpClient, IOptions<AlchemyOptions> options)
{
    private readonly AlchemyOptions _options = options.Value;

    public async Task<string> SearchBranchingGraphAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"insights/app/{_options.AppId}",
            new { Text = query },
            cancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("Insights", out var insights) ||
            insights.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(502, "alchemy_invalid_response", "Alchemy did not return an Insights array.");
        }

        foreach (var insight in insights.EnumerateArray())
        {
            var solutionType = GetString(insight, "SolutionType");
            if (!string.Equals(solutionType, "Branching", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var description = GetString(insight, "Description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }

        throw new ApiException(404, "branching_solution_not_found", "Alchemy did not return a branching solution.");
    }

    public async Task<string> GetBranchGraphAsync(string branchId, string locale, CancellationToken cancellationToken)
    {
        using var document = await GetWrapperAsync(
            $"app/{_options.AppId}/solutions/{locale}/branching/{Uri.EscapeDataString(branchId)}",
            cancellationToken);

        return GetRequiredString(document.RootElement, "SolutionContent");
    }

    public async Task<AlchemyResource> GetResourceAsync(
        string resourceId,
        string locale,
        CancellationToken cancellationToken)
    {
        using var document = await GetWrapperAsync(
            $"app/{_options.AppId}/solutions/{locale}/alchemyresource/{Uri.EscapeDataString(resourceId)}",
            cancellationToken);

        return new AlchemyResource(
            resourceId,
            GetRequiredString(document.RootElement, "SolutionContent"),
            GetString(document.RootElement, "CorrelationId"));
    }

    private async Task<JsonDocument> GetWrapperAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        GetString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw new ApiException(502, "alchemy_invalid_response", $"Alchemy response is missing {propertyName}.");

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record AlchemyResource(string Id, string Html, string? CorrelationId);

using AlchemyProxy.Endpoints;
using AlchemyProxy.Infrastructure;
using AlchemyProxy.Services;
using AlchemyProxy.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AlchemyOptions>(builder.Configuration.GetSection(AlchemyOptions.SectionName));
builder.Services.Configure<LocalStorageOptions>(builder.Configuration.GetSection(LocalStorageOptions.SectionName));

builder.Services.AddHttpClient<AlchemyClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlchemyOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
builder.Services.AddHttpClient();

builder.Services.AddSingleton<FileSolutionSnapshotStore>();
builder.Services.AddSingleton<SqliteSessionStore>();
builder.Services.AddScoped<SolutionLoader>();
builder.Services.AddScoped<TroubleshootingService>();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, code, title) = error switch
        {
            ApiException api => (api.StatusCode, api.Code, api.Message),
            HttpRequestException => (StatusCodes.Status503ServiceUnavailable, "alchemy_unavailable", "Alchemy is unavailable."),
            System.Text.Json.JsonException => (StatusCodes.Status502BadGateway, "alchemy_invalid_response", "Alchemy returned an invalid response."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")
        };

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = error is ApiException ? error.Message : null,
            Type = $"https://alchemyproxy.local/problems/{code}",
            Extensions = { ["code"] = code }
        });
    });
});

await app.Services.GetRequiredService<SqliteSessionStore>().InitializeAsync();

app.MapTroubleshootingEndpoints();
app.MapLegacyAlchemyEndpoints();

app.Run();

public partial class Program;

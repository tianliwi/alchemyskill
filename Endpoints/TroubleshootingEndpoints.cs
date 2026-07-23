using AlchemyProxy.Models;
using AlchemyProxy.Services;

namespace AlchemyProxy.Endpoints;

public static class TroubleshootingEndpoints
{
    public static IEndpointRouteBuilder MapTroubleshootingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/troubleshooting");

        group.MapPost("/sessions", async (
            StartSessionRequest request,
            HttpContext context,
            TroubleshootingService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.StartAsync(request, GetOwner(context), cancellationToken);
            return Results.Created($"/api/v1/troubleshooting/sessions/{response.SessionId}", response);
        });

        group.MapGet("/sessions/{sessionId}", async (
            string sessionId,
            HttpContext context,
            TroubleshootingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(sessionId, GetOwner(context), cancellationToken)));

        group.MapPost("/sessions/{sessionId}/answers", async (
            string sessionId,
            AnswerSessionRequest request,
            HttpContext context,
            TroubleshootingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.AnswerAsync(sessionId, request, GetOwner(context), cancellationToken)));

        group.MapPost("/sessions/{sessionId}/escalations", async (
            string sessionId,
            EscalateSessionRequest request,
            HttpContext context,
            TroubleshootingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.EscalateAsync(sessionId, request, GetOwner(context), cancellationToken)));

        group.MapGet("/sessions/{sessionId}/context", async (
            string sessionId,
            HttpContext context,
            TroubleshootingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetContextAsync(sessionId, GetOwner(context), cancellationToken)));

        return endpoints;
    }

    private static SessionOwner GetOwner(HttpContext context)
    {
        var tenantId = context.Request.Headers["X-Test-Tenant-Id"].FirstOrDefault() ?? "local-tenant";
        var userId = context.Request.Headers["X-Test-User-Id"].FirstOrDefault() ?? "local-user";
        return new SessionOwner(tenantId, userId);
    }
}

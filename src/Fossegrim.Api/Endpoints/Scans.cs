using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Library scans. They run in the background, one at a time: starting one
/// returns 202 with the queued job, and its progress is read from the
/// status endpoints. Admins use /api/admin/scans; other applications use
/// /api/external/scan with an API key (X-Api-Key), which can do nothing else.
/// </summary>
public static class Scans
{
    public static void MapScanEndpoints(this WebApplication app)
    {
        var adminGroup = app.MapGroup("/api/admin/scans")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        adminGroup.MapGet("/", (ScanQueue queue) => Results.Ok(queue.GetStatus()))
            .WithName("GetScanStatus")
            .WithOpenApi();

        adminGroup.MapGet("/{id:guid}", (Guid id, ScanQueue queue) =>
                queue.Find(id) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("GetScan")
            .WithOpenApi();

        adminGroup.MapPost("/", async (StartScanRequest request, ScanRequests requests, ScanQueue queue) =>
                await StartAsync(request.FolderIds, request.Path, request.Full, "admin", requests, queue))
            .WithName("StartScan")
            .WithOpenApi();

        adminGroup.MapPost("/albums/{id:guid}", async (Guid id, ScanRequests requests, ScanQueue queue) =>
                await requests.ForAlbumAsync(id) is { } spec ? Accepted(queue.Enqueue(spec)) : Results.NotFound())
            .WithName("RescanAlbum")
            .WithOpenApi();

        adminGroup.MapPost("/artists/{id:guid}", async (Guid id, ScanRequests requests, ScanQueue queue) =>
                await requests.ForArtistAsync(id) is { } spec ? Accepted(queue.Enqueue(spec)) : Results.NotFound())
            .WithName("RescanArtist")
            .WithOpenApi();

        var externalGroup = app.MapGroup("/api/external/scan")
            .WithTags("External")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthentication.Scheme)
                .RequireAuthenticatedUser());

        // A body is optional: no body (or no path) scans every media folder.
        externalGroup.MapPost("/", async (HttpRequest http, ClaimsPrincipal principal, ScanRequests requests, ScanQueue queue) =>
            {
                var body = http.ContentLength is > 0 || http.HasJsonContentType()
                    ? await http.ReadFromJsonAsync<ExternalScanRequest>()
                    : null;
                return await StartAsync(null, body?.Path, body?.Full ?? false, $"api:{principal.Identity?.Name}", requests, queue);
            })
            .WithName("ExternalStartScan")
            .WithOpenApi();

        externalGroup.MapGet("/", (ScanQueue queue) => Results.Ok(queue.GetStatus()))
            .WithName("ExternalGetScanStatus")
            .WithOpenApi();

        externalGroup.MapGet("/{id:guid}", (Guid id, ScanQueue queue) =>
                queue.Find(id) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("ExternalGetScan")
            .WithOpenApi();
    }

    private static async Task<IResult> StartAsync(IReadOnlyList<Guid>? folderIds, string? path, bool full, string source, ScanRequests requests, ScanQueue queue)
    {
        var (spec, error) = await requests.ForFoldersOrPathAsync(folderIds, path, full, source);
        return spec is null ? Results.BadRequest(new ApiErrorResponse(error!)) : Accepted(queue.Enqueue(spec));
    }

    private static IResult Accepted(ScanJobDto job) => Results.Accepted($"/api/admin/scans/{job.Id}", job);
}

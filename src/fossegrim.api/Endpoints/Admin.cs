using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Endpoints;

public static class Admin
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var adminGroup = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        adminGroup.MapPost("/scan", async (ScanRequest request, MediaLibraryService service) =>
        {
            try
            {
                var result = await service.ScanAndSaveMediaItemsAsync(
                    request.FolderPath,
                    request.MaxDegreeOfParallelism ?? 4);

                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Scan failed"
                );
            }
        })
        .WithName("ScanMediaFolder")
        .WithOpenApi();
    }
}

public record ScanRequest(string FolderPath, int? MaxDegreeOfParallelism = 4);

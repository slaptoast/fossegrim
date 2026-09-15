using Fossegrim.Lib.Data;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

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
                return Results.BadRequest(new ApiErrorResponse(ex.Message));
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new ApiErrorResponse(ex.Message));
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

        adminGroup.MapGet("/folders", async (FossegrimDbContext db) =>
        {
            var folders = await db.MediaFolders
                .OrderBy(f => f.Name)
                .Select(f => new MediaFolderDto(f.Id, f.Name, f.Location, f.DateAdded, f.DateLastScanned))
                .ToListAsync();

            return Results.Ok(folders);
        })
        .WithName("GetAllMediaFolders")
        .WithOpenApi();

        adminGroup.MapGet("/folders/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var folder = await db.MediaFolders
                .Where(f => f.Id == id)
                .Select(f => new MediaFolderDto(f.Id, f.Name, f.Location, f.DateAdded, f.DateLastScanned))
                .FirstOrDefaultAsync();

            return folder is not null ? Results.Ok(folder) : Results.NotFound();
        })
        .WithName("GetMediaFolderById")
        .WithOpenApi();

        adminGroup.MapPost("/folders", async (AddMediaFolderRequest request, FossegrimDbContext db) =>
        {
            var existingFolder = await db.MediaFolders
                .FirstOrDefaultAsync(f => f.Location == request.Location);

            if (existingFolder is not null)
            {
                return Results.Conflict(new ApiErrorResponse("A folder with this location already exists."));
            }

            var folder = new MediaFolder
            {
                Name = request.Name,
                Location = request.Location,
                DateAdded = DateTime.UtcNow
            };

            db.MediaFolders.Add(folder);
            await db.SaveChangesAsync();

            var dto = new MediaFolderDto(folder.Id, folder.Name, folder.Location, folder.DateAdded, folder.DateLastScanned);
            return Results.Created($"/api/admin/folders/{folder.Id}", dto);
        })
        .WithName("AddMediaFolder")
        .WithOpenApi();

        adminGroup.MapDelete("/folders/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var folder = await db.MediaFolders.FindAsync(id);
            if (folder is null)
            {
                return Results.NotFound();
            }

            db.MediaFolders.Remove(folder);
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("DeleteMediaFolder")
        .WithOpenApi();
    }
}

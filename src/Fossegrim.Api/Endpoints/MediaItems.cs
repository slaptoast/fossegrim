using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class MediaItems
{
    public static void MapMediaItemEndpoints(this WebApplication app)
    {
        var mediaItemGroup = app.MapGroup("/api/mediaitems")
            .WithTags("MediaItems")
            .RequireAuthorization();

        mediaItemGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var mediaItems = await db.MediaItems
                .Select(m => new MediaItemDto(
                    m.Id,
                    m.Title,
                    m.ArtistName,
                    m.Album,
                    m.DateAdded,
                    m.LastModified,
                    m.Year,
                    m.Track,
                    m.Genre,
                    m.Duration,
                    m.Bitrate,
                    m.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name)),
                    m.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year))))
                .ToListAsync();
            return Results.Ok(mediaItems);
        })
        .WithName("GetAllMediaItems")
        .WithOpenApi();

        mediaItemGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var mediaItem = await db.MediaItems
                .Where(m => m.Id == id)
                .Select(m => new MediaItemDto(
                    m.Id,
                    m.Title,
                    m.ArtistName,
                    m.Album,
                    m.DateAdded,
                    m.LastModified,
                    m.Year,
                    m.Track,
                    m.Genre,
                    m.Duration,
                    m.Bitrate,
                    m.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name)),
                    m.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year))))
                .FirstOrDefaultAsync();

            return mediaItem is not null ? Results.Ok(mediaItem) : Results.NotFound();
        })
        .WithName("GetMediaItemById")
        .WithOpenApi();

        mediaItemGroup.MapPut("/tags", async (
            UpdateMediaItemTagsRequest request,
            MediaLibraryService service) =>
        {
            var items = request.Items.ToList();
            if (items.Count == 0)
            {
                return Results.BadRequest(new ApiErrorResponse("At least one item is required."));
            }

            var result = await service.UpdateTagsAsync(items);
            return Results.Ok(result);
        })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .WithName("UpdateMediaItemTags")
        .WithOpenApi();
    }
}

using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services.Library;

namespace Fossegrim.Api.Endpoints;

public static class Streaming
{
    public static void MapStreamingEndpoints(this WebApplication app)
    {
        app.MapGet("/stream/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var mediaItem = await db.MediaItems.FindAsync(id);

            if (mediaItem is null || string.IsNullOrWhiteSpace(mediaItem.FileLocation) || !File.Exists(mediaItem.FileLocation))
            {
                return Results.NotFound();
            }

            if (AudioFormats.ContentType(mediaItem.FileLocation) is not { } contentType)
            {
                return Results.BadRequest("This file type can't be streamed.");
            }

            return Results.File(mediaItem.FileLocation, contentType, enableRangeProcessing: true);
        })
        .WithName("StreamMediaFile")
        .WithOpenApi()
        .RequireAuthorization();
    }
}

using Fossegrim.Lib.Data;
using Fossegrim.Lib.Enums;

namespace Fossegrim.Web.Endpoints;

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

            var fileType = MediaFileTypeHelper.GetFileType(Path.GetExtension(mediaItem.FileLocation));
            if (fileType != MediaFileType.MP3)
            {
                return Results.BadRequest("Only MP3 files can be streamed.");
            }

            return Results.File(mediaItem.FileLocation, "audio/mpeg", enableRangeProcessing: true);
        })
        .WithName("StreamMediaFile")
        .WithOpenApi();
    }
}

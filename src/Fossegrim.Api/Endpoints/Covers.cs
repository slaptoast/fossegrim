using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Endpoints;

public static class Covers
{
    public static void MapCoverEndpoints(this WebApplication app)
    {
        app.MapGet("/cover/{albumId:guid}", async (Guid albumId, MediaLibraryService mediaLibraryService) =>
        {
            var cover = await mediaLibraryService.GetAlbumCoverAsync(albumId);
            if (cover is null)
            {
                return Results.NotFound();
            }

            return Results.File(cover.Value.Data, cover.Value.ContentType);
        })
        .WithName("GetAlbumCover")
        .WithOpenApi()
        .RequireAuthorization();
    }
}

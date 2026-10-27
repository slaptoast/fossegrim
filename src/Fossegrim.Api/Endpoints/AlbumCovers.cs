using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Changing an album's cover: searching the free cover services and saving a
/// result or an uploaded image into the album's files. Admin only, like tag
/// editing; searches also use up limits every user of the server shares.
/// </summary>
public static class AlbumCovers
{
    public static void MapAlbumCoverEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/covers")
            .WithTags("Covers")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        // Where each provider stands, to show before (and between) searches.
        group.MapGet("/providers", (CoverSearchService search) => Results.Ok(search.GetStatuses()))
            .WithName("GetCoverProviders")
            .WithOpenApi();

        group.MapGet("/search", async (string? album, string? artist, CoverSearchService search, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(album))
            {
                return Results.BadRequest(new ApiErrorResponse("Enter an album name to search for."));
            }

            return Results.Ok(await search.SearchAsync(album, artist, cancellationToken));
        })
        .WithName("SearchCovers")
        .WithOpenApi();

        // The body is the image itself (JPEG or PNG; the browser shrinks large ones first).
        group.MapPut("/albums/{albumId:guid}", async (Guid albumId, HttpRequest request, MediaLibraryService library, CancellationToken cancellationToken) =>
        {
            if (request.ContentLength > CoverImages.MaxBytes)
            {
                return TooLarge();
            }

            var data = await CoverDownloader.ReadLimitedAsync(request.Body, cancellationToken);
            if (data is null)
            {
                return TooLarge();
            }

            return await SaveAsync(albumId, data, library);
        })
        .WithName("UploadAlbumCover")
        .WithOpenApi();

        group.MapPost("/albums/{albumId:guid}/from-search", async (Guid albumId, SetCoverFromSearchRequest body, CoverDownloader downloader, MediaLibraryService library, CancellationToken cancellationToken) =>
        {
            var download = await downloader.DownloadAsync(body.ImageUrl ?? "", cancellationToken);
            if (download.Data is null)
            {
                return Results.BadRequest(new ApiErrorResponse(download.Error ?? "Couldn't download the image."));
            }

            return await SaveAsync(albumId, download.Data, library);
        })
        .WithName("SetAlbumCoverFromSearch")
        .WithOpenApi();
    }

    private static async Task<IResult> SaveAsync(Guid albumId, byte[] data, MediaLibraryService library)
    {
        var mimeType = CoverImages.DetectMimeType(data);
        if (mimeType is null)
        {
            return Results.BadRequest(new ApiErrorResponse("Covers must be JPEG or PNG images."));
        }

        var result = await library.SetAlbumCoverAsync(albumId, data, mimeType);
        return result is null ? Results.NotFound(new ApiErrorResponse("Album not found.")) : Results.Ok(result);
    }

    private static IResult TooLarge() =>
        Results.Json(new ApiErrorResponse("Covers can be at most 10 MB."), statusCode: StatusCodes.Status413PayloadTooLarge);
}

using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Microsoft.Net.Http.Headers;

namespace Fossegrim.Api.Endpoints;

public static class Covers
{
    public static void MapCoverEndpoints(this WebApplication app)
    {
        // ?size=N shrinks the cover so its longest side is at most N, rounded
        // up to one of CoverCache.Sizes (above the largest: full size). The
        // ETag changes with the cover, so clients can revalidate cheaply.
        app.MapGet("/cover/{albumId:guid}", async (Guid albumId, int? size, HttpRequest request, CoverCache covers, CancellationToken cancellationToken) =>
        {
            if (size is <= 0)
            {
                return Results.BadRequest(new ApiErrorResponse("size must be a positive number of pixels."));
            }

            var servedSize = CoverCache.SizeFor(size);
            if (await covers.GetVersionAsync(albumId) is not { } coverVersion)
            {
                return Results.NotFound();
            }

            // Differs per size, as each size is a different image.
            var etag = new EntityTagHeaderValue($"\"{coverVersion}-{servedSize?.ToString() ?? "full"}\"");
            if (EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out var sent) &&
                sent.Any(t => t.Compare(etag, useStrongComparison: false) || t.Equals(EntityTagHeaderValue.Any)))
            {
                SetCaching(request.HttpContext.Response, etag);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            if (await covers.GetAsync(albumId, servedSize, coverVersion, cancellationToken) is not { } cover)
            {
                return Results.NotFound();
            }

            SetCaching(request.HttpContext.Response, etag);
            return Results.File(cover.Path, cover.ContentType);
        })
        .WithName("GetAlbumCover")
        .WithOpenApi()
        .RequireAuthorization();
    }

    // Private: the response needs the caller's token. no-cache: keep it, but
    // check the ETag before reusing it, so an edited cover shows up at once.
    private static void SetCaching(HttpResponse response, EntityTagHeaderValue etag)
    {
        response.Headers.ETag = etag.ToString();
        response.Headers.CacheControl = "private, no-cache";
    }
}

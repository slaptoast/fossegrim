using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services.Library;

namespace Fossegrim.Api.Endpoints;

/// <summary>Library search across artists, albums and tracks, and the shared handling of the list endpoints.</summary>
public static class Browse
{
    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 100;

    public static void MapSearchEndpoints(this WebApplication app)
    {
        // The best matches of each kind, like Subsonic's search3. limit= is
        // per kind (default 10, max 100); for more of one kind, page through
        // its list endpoint with the same q.
        app.MapGet("/api/search", async (string? q, int? limit, ClaimsPrincipal principal, LibraryBrowser browser, CancellationToken cancellationToken) =>
        {
            if (SearchIndex.MatchQuery(q) is null)
            {
                return Results.BadRequest(new ApiErrorResponse("q must contain a word to search for."));
            }
            if (UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var size = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaxSearchLimit);
            BrowseRequest Request(IReadOnlyList<string> sorts) => LibraryBrowser.Parse(q, null, null, 1, size, sorts).Request!;

            return Results.Ok(new SearchResultDto(
                await browser.ArtistsAsync(Request(LibrarySorts.ForArtists), userId, cancellationToken),
                await browser.AlbumsAsync(Request(LibrarySorts.ForAlbums), userId, cancellationToken),
                await browser.TracksAsync(Request(LibrarySorts.ForTracks), userId, cancellationToken)));
        })
        .WithTags("Search")
        .WithName("Search")
        .WithOpenApi()
        .RequireAuthorization();
    }

    internal static async Task<IResult> ListAsync<T>(
        string? q,
        string? sort,
        string? order,
        int? page,
        int? pageSize,
        IReadOnlyList<string> sorts,
        ClaimsPrincipal principal,
        Func<BrowseRequest, string, Task<PagedResult<T>>> list)
    {
        var (request, error) = LibraryBrowser.Parse(q, sort, order, page, pageSize, sorts);
        if (request is null)
        {
            return Results.BadRequest(new ApiErrorResponse(error!));
        }
        if (UserId(principal) is not { } userId)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(await list(request, userId));
    }

    // The bearer handler maps the token's "sub" to NameIdentifier.
    private static string? UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier);
}

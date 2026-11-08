namespace Fossegrim.Contracts.Dtos;

// One page of a list. Page is 1-based; TotalCount counts every match, across all pages.
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
}

// An album in a list: its tracks come from /api/albums/{id}/tracks.
// LastPlayedAt is when the caller last played any of its tracks (null if never).
public record AlbumListItemDto(
    Guid Id,
    string Name,
    int? Year,
    string? Genre,
    DateTime? DateAdded,
    int TrackCount,
    IEnumerable<ArtistSummaryDto> Artists,
    DateTime? LastPlayedAt = null);

// An artist in a list: its tracks come from /api/artists/{id}/tracks.
// LastPlayedAt is when the caller last played any of its tracks (null if never).
public record ArtistListItemDto(Guid Id, string Name, DateTime? DateAdded, int AlbumCount, int TrackCount, DateTime? LastPlayedAt = null);

// /api/search: the best matches of each kind. Each list endpoint takes the
// same q (and a page) for more of one kind.
public record SearchResultDto(
    PagedResult<ArtistListItemDto> Artists,
    PagedResult<AlbumListItemDto> Albums,
    PagedResult<MediaItemDto> Tracks);

/// <summary>
/// The sort= values the list endpoints take. Each list accepts the ones that
/// apply to it (see <see cref="ForAlbums"/> and the others); order= is
/// "asc" or "desc", defaulting to <see cref="DefaultOrder"/>. With q= and no
/// sort, results come best match first ("relevance"). listened=true leaves
/// out what the caller has never played; sort=recent with it lists the most
/// recently played first (order=desc) or the longest since played (asc).
/// favorites=true keeps only the caller's favourites.
/// </summary>
public static class LibrarySorts
{
    public const string Name = "name";
    public const string Title = "title";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string Year = "year";
    // When it was added to the library.
    public const string Added = "added";
    // How often the caller has played it.
    public const string Played = "played";
    // When the caller last played it.
    public const string Recent = "recent";
    // When the caller favourited it (not favourited: last).
    public const string Favorited = "favorited";
    public const string Relevance = "relevance";

    public const string Ascending = "asc";
    public const string Descending = "desc";

    public static readonly IReadOnlyList<string> ForAlbums = [Name, Artist, Year, Added, Played, Recent, Favorited, Relevance];
    public static readonly IReadOnlyList<string> ForArtists = [Name, Added, Played, Recent, Favorited, Relevance];
    public static readonly IReadOnlyList<string> ForTracks = [Title, Artist, Album, Year, Added, Played, Recent, Favorited, Relevance];

    // Newest, most played and most recent first; names A to Z.
    public static string DefaultOrder(string sort) => sort is Year or Added or Played or Recent or Favorited ? Descending : Ascending;
}

// The caller's favourites, as ids, so a client can show which items have a
// filled heart. The lists themselves come from the list endpoints with
// favorites=true (sort=favorited for newest first).
public record FavoritesDto(IReadOnlyList<Guid> TrackIds, IReadOnlyList<Guid> AlbumIds, IReadOnlyList<Guid> ArtistIds);

// The {kind} in /api/favorites/{kind}/{id}.
public static class FavoriteKinds
{
    public const string Tracks = "tracks";
    public const string Albums = "albums";
    public const string Artists = "artists";
}

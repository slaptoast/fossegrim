using System.Linq.Expressions;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>What a list request asked for, checked: see <see cref="LibraryBrowser.Parse"/>.</summary>
/// Listened leaves out what the caller has never played; FavoritesOnly keeps only the caller's favourites.
public record BrowseRequest(string? Match, string Sort, bool Descending, int Page, int PageSize, bool Listened = false, bool FavoritesOnly = false)
{
    public int Skip => (Page - 1) * PageSize;
}

/// <summary>
/// Searching, sorting and paging the library's albums, artists and tracks
/// for the list endpoints. Play-based sorts use the caller's own listens.
/// </summary>
public class LibraryBrowser
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    private readonly FossegrimDbContext _db;
    private readonly SearchIndex _index;

    public LibraryBrowser(FossegrimDbContext db, SearchIndex index)
    {
        _db = db;
        _index = index;
    }

    /// <summary>
    /// Checks a list request's query string. Without a sort, a search sorts
    /// by relevance and anything else by name (title for tracks); relevance
    /// without a search falls back the same way. Ranked search results can't
    /// be filtered, so with favourites relevance (and no sort) means newest
    /// favourite first, and with listened it means recent.
    /// </summary>
    public static (BrowseRequest? Request, string? Error) Parse(
        string? q, string? sort, string? order, int? page, int? pageSize, IReadOnlyList<string> sorts, bool listened = false, bool favorites = false)
    {
        var match = SearchIndex.MatchQuery(q);
        var fallback = favorites ? LibrarySorts.Favorited : sorts[0];
        var chosen = string.IsNullOrWhiteSpace(sort) ? (match is null || favorites ? fallback : LibrarySorts.Relevance) : sort.Trim().ToLowerInvariant();
        if (!sorts.Contains(chosen))
        {
            return (null, $"sort must be one of: {string.Join(", ", sorts)}.");
        }
        if (chosen == LibrarySorts.Relevance && (match is null || favorites))
        {
            chosen = fallback;
        }
        if (chosen == LibrarySorts.Relevance && listened)
        {
            chosen = LibrarySorts.Recent;
        }

        var direction = string.IsNullOrWhiteSpace(order) ? LibrarySorts.DefaultOrder(chosen) : order.Trim().ToLowerInvariant();
        if (direction is not (LibrarySorts.Ascending or LibrarySorts.Descending))
        {
            return (null, "order must be asc or desc.");
        }

        return (new BrowseRequest(
            match,
            chosen,
            direction == LibrarySorts.Descending,
            Math.Max(page ?? 1, 1),
            Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
            listened,
            favorites), null);
    }

    public async Task<PagedResult<AlbumListItemDto>> AlbumsAsync(BrowseRequest request, string userId, CancellationToken cancellationToken = default)
    {
        if (request.Sort == LibrarySorts.Relevance)
        {
            var (ids, total) = await _index.RankedAsync(SearchKind.Albums, request.Match!, request.Skip, request.PageSize, cancellationToken);
            var found = await _db.Albums.Where(al => ids.Contains(al.Id)).Select(AlbumItem).ToListAsync(cancellationToken);
            return Page(await WithLastPlayedAsync(InOrder(found, ids, a => a.Id), userId, cancellationToken), total, request);
        }

        var albums = _db.Albums.AsQueryable();
        if (request.Match is { } match)
        {
            var matching = _index.Matching(SearchKind.Albums, match);
            albums = albums.Where(al => matching.Contains(al.Id));
        }

        var plays = _db.PlayEvents.Where(pe => pe.UserId == userId);
        var favorites = _db.FavoriteAlbums.Where(f => f.UserId == userId);
        if (request.Listened)
        {
            albums = albums.Where(al => plays.Any(pe => pe.MediaItem!.Albums.Any(x => x.Id == al.Id)));
        }
        if (request.FavoritesOnly)
        {
            albums = albums.Where(al => favorites.Any(f => f.AlbumId == al.Id));
        }
        var sorted = request.Sort switch
        {
            // By the first artist A to Z. The collation must wrap the subquery: one inside it doesn't reach the ORDER BY.
            LibrarySorts.Artist => albums.Sort(al => EF.Functions.Collate(al.Artists.OrderBy(a => EF.Functions.Collate(a.Name, "NOCASE")).Select(a => a.Name).FirstOrDefault(), "NOCASE"), request.Descending),
            LibrarySorts.Year => albums.Sort(al => al.Year, request.Descending),
            LibrarySorts.Added => albums.Sort(al => al.DateAdded, request.Descending),
            LibrarySorts.Played => albums.Sort(al => plays.Count(pe => pe.MediaItem!.Albums.Any(x => x.Id == al.Id)), request.Descending),
            LibrarySorts.Recent => albums.Sort(al => plays.Where(pe => pe.MediaItem!.Albums.Any(x => x.Id == al.Id)).Max(pe => (DateTime?)pe.PlayedAt), request.Descending),
            LibrarySorts.Favorited => albums.Sort(al => favorites.Where(f => f.AlbumId == al.Id).Select(f => (DateTime?)f.CreatedAt).FirstOrDefault(), request.Descending),
            _ => albums.Sort(al => EF.Functions.Collate(al.Name, "NOCASE"), request.Descending),
        };

        var count = await albums.CountAsync(cancellationToken);
        var items = await sorted
            .ThenBy(al => EF.Functions.Collate(al.Name, "NOCASE")).ThenBy(al => al.Id)
            .Skip(request.Skip).Take(request.PageSize)
            .Select(AlbumItem)
            .ToListAsync(cancellationToken);
        return Page(await WithLastPlayedAsync(items, userId, cancellationToken), count, request);
    }

    public async Task<PagedResult<ArtistListItemDto>> ArtistsAsync(BrowseRequest request, string userId, CancellationToken cancellationToken = default)
    {
        if (request.Sort == LibrarySorts.Relevance)
        {
            var (ids, total) = await _index.RankedAsync(SearchKind.Artists, request.Match!, request.Skip, request.PageSize, cancellationToken);
            var found = await _db.Artists.Where(a => ids.Contains(a.Id)).Select(ArtistItem).ToListAsync(cancellationToken);
            return Page(await WithLastPlayedAsync(InOrder(found, ids, a => a.Id), userId, cancellationToken), total, request);
        }

        var artists = _db.Artists.AsQueryable();
        if (request.Match is { } match)
        {
            var matching = _index.Matching(SearchKind.Artists, match);
            artists = artists.Where(a => matching.Contains(a.Id));
        }

        var plays = _db.PlayEvents.Where(pe => pe.UserId == userId);
        var favorites = _db.FavoriteArtists.Where(f => f.UserId == userId);
        if (request.Listened)
        {
            artists = artists.Where(a => plays.Any(pe => pe.MediaItem!.Artists.Any(x => x.Id == a.Id)));
        }
        if (request.FavoritesOnly)
        {
            artists = artists.Where(a => favorites.Any(f => f.ArtistId == a.Id));
        }
        var sorted = request.Sort switch
        {
            LibrarySorts.Added => artists.Sort(a => a.DateAdded, request.Descending),
            LibrarySorts.Played => artists.Sort(a => plays.Count(pe => pe.MediaItem!.Artists.Any(x => x.Id == a.Id)), request.Descending),
            LibrarySorts.Recent => artists.Sort(a => plays.Where(pe => pe.MediaItem!.Artists.Any(x => x.Id == a.Id)).Max(pe => (DateTime?)pe.PlayedAt), request.Descending),
            LibrarySorts.Favorited => artists.Sort(a => favorites.Where(f => f.ArtistId == a.Id).Select(f => (DateTime?)f.CreatedAt).FirstOrDefault(), request.Descending),
            _ => artists.Sort(a => EF.Functions.Collate(a.Name, "NOCASE"), request.Descending),
        };

        var count = await artists.CountAsync(cancellationToken);
        var items = await sorted
            .ThenBy(a => EF.Functions.Collate(a.Name, "NOCASE")).ThenBy(a => a.Id)
            .Skip(request.Skip).Take(request.PageSize)
            .Select(ArtistItem)
            .ToListAsync(cancellationToken);
        return Page(await WithLastPlayedAsync(items, userId, cancellationToken), count, request);
    }

    public async Task<PagedResult<MediaItemDto>> TracksAsync(BrowseRequest request, string userId, CancellationToken cancellationToken = default)
    {
        if (request.Sort == LibrarySorts.Relevance)
        {
            var (ids, total) = await _index.RankedAsync(SearchKind.Tracks, request.Match!, request.Skip, request.PageSize, cancellationToken);
            var found = await _db.MediaItems.Where(m => ids.Contains(m.Id)).Select(MediaItemMapping.ToDto).ToListAsync(cancellationToken);
            return Page(await WithLastPlayedAsync(InOrder(found, ids, m => m.Id), userId, cancellationToken), total, request);
        }

        var tracks = _db.MediaItems.AsQueryable();
        if (request.Match is { } match)
        {
            var matching = _index.Matching(SearchKind.Tracks, match);
            tracks = tracks.Where(m => matching.Contains(m.Id));
        }

        var plays = _db.PlayEvents.Where(pe => pe.UserId == userId);
        var favorites = _db.FavoriteTracks.Where(f => f.UserId == userId);
        if (request.Listened)
        {
            tracks = tracks.Where(m => plays.Any(pe => pe.MediaItemId == m.Id));
        }
        if (request.FavoritesOnly)
        {
            tracks = tracks.Where(m => favorites.Any(f => f.MediaItemId == m.Id));
        }
        var sorted = request.Sort switch
        {
            LibrarySorts.Artist => tracks.Sort(m => EF.Functions.Collate(m.ArtistName, "NOCASE"), request.Descending),
            LibrarySorts.Album => tracks.Sort(m => EF.Functions.Collate(m.Album, "NOCASE"), request.Descending),
            LibrarySorts.Year => tracks.Sort(m => m.Year, request.Descending),
            LibrarySorts.Added => tracks.Sort(m => m.DateAdded, request.Descending),
            LibrarySorts.Played => tracks.Sort(m => plays.Count(pe => pe.MediaItemId == m.Id), request.Descending),
            LibrarySorts.Recent => tracks.Sort(m => plays.Where(pe => pe.MediaItemId == m.Id).Max(pe => (DateTime?)pe.PlayedAt), request.Descending),
            LibrarySorts.Favorited => tracks.Sort(m => favorites.Where(f => f.MediaItemId == m.Id).Select(f => (DateTime?)f.CreatedAt).FirstOrDefault(), request.Descending),
            _ => tracks.Sort(m => EF.Functions.Collate(m.Title, "NOCASE"), request.Descending),
        };

        // Within an album or artist, keep the album's own order.
        var count = await tracks.CountAsync(cancellationToken);
        var items = await sorted
            .ThenBy(m => EF.Functions.Collate(m.Album, "NOCASE"))
            .ThenBy(m => m.Disc ?? 1).ThenBy(m => m.Track ?? int.MaxValue)
            .ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE")).ThenBy(m => m.Id)
            .Skip(request.Skip).Take(request.PageSize)
            .Select(MediaItemMapping.ToDto)
            .ToListAsync(cancellationToken);
        return Page(await WithLastPlayedAsync(items, userId, cancellationToken), count, request);
    }

    private static readonly Expression<Func<Album, AlbumListItemDto>> AlbumItem = al => new AlbumListItemDto(
        al.Id,
        al.Name,
        al.Year,
        al.Genre,
        al.DateAdded,
        al.MediaItems.Count,
        al.Artists.OrderBy(a => a.Name).Select(a => new ArtistSummaryDto(a.Id, a.Name)),
        null);

    private static readonly Expression<Func<Artist, ArtistListItemDto>> ArtistItem = a => new ArtistListItemDto(
        a.Id,
        a.Name,
        a.DateAdded,
        a.Albums.Count,
        a.MediaItems.Count,
        null);

    // When the caller last played each item on a page (one query per page).
    private async Task<List<AlbumListItemDto>> WithLastPlayedAsync(List<AlbumListItemDto> items, string userId, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.Id).ToList();
        var last = await _db.PlayEvents
            .Where(pe => pe.UserId == userId)
            .SelectMany(pe => pe.MediaItem!.Albums.Where(al => ids.Contains(al.Id)), (pe, al) => new { al.Id, pe.PlayedAt })
            .GroupBy(x => x.Id)
            .Select(g => new { g.Key, Last = g.Max(x => x.PlayedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, cancellationToken);
        return items.Select(i => last.TryGetValue(i.Id, out var at) ? i with { LastPlayedAt = at } : i).ToList();
    }

    private async Task<List<ArtistListItemDto>> WithLastPlayedAsync(List<ArtistListItemDto> items, string userId, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.Id).ToList();
        var last = await _db.PlayEvents
            .Where(pe => pe.UserId == userId)
            .SelectMany(pe => pe.MediaItem!.Artists.Where(a => ids.Contains(a.Id)), (pe, a) => new { a.Id, pe.PlayedAt })
            .GroupBy(x => x.Id)
            .Select(g => new { g.Key, Last = g.Max(x => x.PlayedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, cancellationToken);
        return items.Select(i => last.TryGetValue(i.Id, out var at) ? i with { LastPlayedAt = at } : i).ToList();
    }

    private async Task<List<MediaItemDto>> WithLastPlayedAsync(List<MediaItemDto> items, string userId, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.Id).ToList();
        var last = await _db.PlayEvents
            .Where(pe => pe.UserId == userId && ids.Contains(pe.MediaItemId))
            .GroupBy(pe => pe.MediaItemId)
            .Select(g => new { g.Key, Last = g.Max(pe => pe.PlayedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, cancellationToken);
        return items.Select(i => last.TryGetValue(i.Id, out var at) ? i with { LastPlayedAt = at } : i).ToList();
    }

    private static PagedResult<T> Page<T>(IReadOnlyList<T> items, int total, BrowseRequest request) =>
        new(items, total, request.Page, request.PageSize);

    // The rows for a page of ranked ids, in rank order.
    private static List<T> InOrder<T>(List<T> found, List<Guid> ids, Func<T, Guid> id)
    {
        var byId = found.ToDictionary(id);
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }
}

internal static class SortExtensions
{
    public static IOrderedQueryable<T> Sort<T, TKey>(this IQueryable<T> source, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);
}

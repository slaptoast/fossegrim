using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Lib.Services.Library;

public enum SearchKind
{
    Tracks,
    Albums,
    Artists,
}

/// <summary>
/// SQLite FTS5 full-text indexes over tracks, albums and artists (tables
/// TrackSearch, AlbumSearch and ArtistSearch; see the LibrarySearch
/// migration). Matching ignores case and accents, and each word in a query
/// matches by prefix, so "bjo hom" finds Björk's Homogenic.
///
/// The indexes aren't updated row by row. They're rebuilt as a whole after
/// anything that changes what they hold: a scan, a tag edit, and at startup
/// if they're out of step with the library (e.g. after this was first
/// deployed). A rebuild of a large library takes about a second.
/// </summary>
public class SearchIndex
{
    private const int MaxTerms = 10;

    private readonly FossegrimDbContext _db;

    public SearchIndex(FossegrimDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The FTS5 query for what a user typed: every word must match (as a
    /// prefix) somewhere. Null when there's nothing to search for. Words are
    /// quoted, so FTS5 operators and syntax in them are taken literally.
    /// </summary>
    public static string? MatchQuery(string? q)
    {
        var terms = (q ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Replace("\"", ""))
            // A word of only punctuation gives FTS5 an empty phrase.
            .Where(word => word.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)
            .Select(word => $"\"{word}\"*")
            .ToList();
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    /// <summary>Ids matching <paramref name="match"/>, to compose into an EF query (it becomes an IN subquery).</summary>
    public IQueryable<Guid> Matching(SearchKind kind, string match) =>
        _db.Database.SqlQueryRaw<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"{Table(kind)}\" WHERE \"{Table(kind)}\" MATCH {{0}}", match);

    /// <summary>One page of the ids matching <paramref name="match"/>, best match first, and how many match in all.</summary>
    public async Task<(List<Guid> Ids, int Total)> RankedAsync(SearchKind kind, string match, int skip, int take, CancellationToken cancellationToken = default)
    {
        var table = Table(kind);
        var total = await _db.Database
            .SqlQueryRaw<int>($"SELECT count(*) AS \"Value\" FROM \"{table}\" WHERE \"{table}\" MATCH {{0}}", match)
            .SingleAsync(cancellationToken);
        var ids = await _db.Database
            .SqlQueryRaw<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"{table}\" WHERE \"{table}\" MATCH {{0}} ORDER BY {Rank(kind)} LIMIT {{1}} OFFSET {{2}}", match, take, skip)
            .ToListAsync(cancellationToken);
        return (ids, total);
    }

    /// <summary>Rebuilds all three indexes from the library, in one transaction.</summary>
    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlRawAsync("""
            DELETE FROM "TrackSearch";
            INSERT INTO "TrackSearch" ("Id", "Title", "Artist", "Album", "Genre", "Year")
            SELECT m."Id",
                   coalesce(m."Title", ''),
                   trim(coalesce(m."ArtistName", '') || ' ' || coalesce((
                       SELECT group_concat(a."Name", ' ') FROM "MediaItemArtists" ma
                       JOIN "Artists" a ON a."Id" = ma."ArtistsId"
                       WHERE ma."MediaItemsId" = m."Id" AND a."Name" <> coalesce(m."ArtistName", '')), '')),
                   coalesce(m."Album", ''),
                   coalesce(m."Genre", ''),
                   coalesce(CAST(m."Year" AS TEXT), '')
            FROM "MediaItems" m;

            DELETE FROM "AlbumSearch";
            INSERT INTO "AlbumSearch" ("Id", "Name", "Artists", "Genre", "Year", "Label")
            SELECT al."Id",
                   al."Name",
                   coalesce((
                       SELECT group_concat(a."Name", ' ') FROM "AlbumArtists" aa
                       JOIN "Artists" a ON a."Id" = aa."ArtistsId"
                       WHERE aa."AlbumsId" = al."Id"), ''),
                   coalesce(al."Genre", ''),
                   coalesce(CAST(al."Year" AS TEXT), ''),
                   coalesce(al."Label", '')
            FROM "Albums" al;

            DELETE FROM "ArtistSearch";
            INSERT INTO "ArtistSearch" ("Id", "Name")
            SELECT a."Id", a."Name" FROM "Artists" a;
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Rebuilds the indexes if they don't hold one row per track, album and artist.</summary>
    public async Task EnsureBuiltAsync(CancellationToken cancellationToken = default)
    {
        var inStep = await _db.Database.SqlQueryRaw<bool>("""
            SELECT (SELECT count(*) FROM "MediaItems") = (SELECT count(*) FROM "TrackSearch")
               AND (SELECT count(*) FROM "Albums") = (SELECT count(*) FROM "AlbumSearch")
               AND (SELECT count(*) FROM "Artists") = (SELECT count(*) FROM "ArtistSearch") AS "Value"
            """).SingleAsync(cancellationToken);
        if (!inStep)
        {
            await RebuildAsync(cancellationToken);
        }
    }

    private static string Table(SearchKind kind) => kind switch
    {
        SearchKind.Tracks => "TrackSearch",
        SearchKind.Albums => "AlbumSearch",
        SearchKind.Artists => "ArtistSearch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // bm25 weights, one per column (Id first, unindexed): a match in the
    // title or name counts most, then artist and album, then the rest.
    private static string Rank(SearchKind kind) => kind switch
    {
        SearchKind.Tracks => "bm25(\"TrackSearch\", 0, 10, 4, 4, 1, 1)",
        SearchKind.Albums => "bm25(\"AlbumSearch\", 0, 10, 5, 1, 1, 1)",
        SearchKind.Artists => "bm25(\"ArtistSearch\", 0, 1)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

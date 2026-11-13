using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// The hover cards' info: library facts, the caller's plays, and a
/// Wikipedia summary cached on the album or artist. A lookup refreshes the
/// summary once it's a month old; "nothing found" is cached too, so an
/// obscure album isn't looked up on every hover. A failed lookup (offline,
/// rate limited) changes nothing, so the next hover tries again.
/// </summary>
public class LibraryInfoService
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    private readonly FossegrimDbContext _db;
    private readonly WikipediaInfoService _wikipedia;
    private readonly TimeProvider _time;

    public LibraryInfoService(FossegrimDbContext db, WikipediaInfoService wikipedia, TimeProvider time)
    {
        _db = db;
        _wikipedia = wikipedia;
        _time = time;
    }

    public async Task<AlbumInfoDto?> AlbumAsync(Guid id, string userId, bool lookup, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums.Include(al => al.Artists).FirstOrDefaultAsync(al => al.Id == id, cancellationToken);
        if (album is null)
        {
            return null;
        }

        var artists = album.Artists.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (lookup && IsStale(album.InfoFetchedAt))
        {
            var found = await _wikipedia.AlbumAsync(album.Name, artists.FirstOrDefault(), cancellationToken);
            if (found.Outcome != InfoLookupOutcome.Unavailable)
            {
                album.Summary = found.Summary;
                album.InfoUrl = found.Url;
                album.InfoFetchedAt = Now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        var tracks = _db.MediaItems.Where(m => m.Albums.Any(al => al.Id == id));
        var durations = await tracks.Select(m => m.Duration).ToListAsync(cancellationToken);
        var plays = _db.PlayEvents.Where(pe => pe.UserId == userId && pe.MediaItem!.Albums.Any(al => al.Id == id));
        var length = durations.Any(d => d is not null) ? durations.Aggregate(TimeSpan.Zero, (sum, d) => sum + (d ?? TimeSpan.Zero)) : (TimeSpan?)null;

        return new AlbumInfoDto(
            album.Id, album.Name, artists, album.Year, album.Genre, album.Label,
            durations.Count, length,
            await plays.CountAsync(cancellationToken),
            await plays.MaxAsync(pe => (DateTime?)pe.PlayedAt, cancellationToken),
            album.Summary, album.InfoUrl, IsStale(album.InfoFetchedAt));
    }

    public async Task<ArtistInfoDto?> ArtistAsync(Guid id, string userId, bool lookup, CancellationToken cancellationToken = default)
    {
        var artist = await _db.Artists.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artist is null)
        {
            return null;
        }

        if (lookup && IsStale(artist.InfoFetchedAt))
        {
            var found = await _wikipedia.ArtistAsync(artist.Name, cancellationToken);
            if (found.Outcome != InfoLookupOutcome.Unavailable)
            {
                artist.Bio = found.Summary;
                artist.InfoUrl = found.Url;
                artist.Country = found.Country ?? artist.Country;
                artist.InfoFetchedAt = Now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        var plays = _db.PlayEvents.Where(pe => pe.UserId == userId && pe.MediaItem!.Artists.Any(a => a.Id == id));
        return new ArtistInfoDto(
            artist.Id, artist.Name, artist.Country,
            await _db.Albums.CountAsync(al => al.Artists.Any(a => a.Id == id), cancellationToken),
            await _db.MediaItems.CountAsync(m => m.Artists.Any(a => a.Id == id), cancellationToken),
            await plays.CountAsync(cancellationToken),
            await plays.MaxAsync(pe => (DateTime?)pe.PlayedAt, cancellationToken),
            artist.Bio, artist.InfoUrl, IsStale(artist.InfoFetchedAt));
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private bool IsStale(DateTime? fetchedAt) => fetchedAt is not { } at || Now - at >= RefreshAfter;
}

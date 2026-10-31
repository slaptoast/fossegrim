using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Lib.Services.Library;

/// <summary>
/// Finds or creates the Artist and Album rows a track's tag values name
/// (matched case-insensitively), and removes ones left with no tracks.
/// </summary>
public class LibraryLinks
{
    private readonly FossegrimDbContext _db;
    private readonly Dictionary<string, Artist> _artistsByName;
    private readonly Dictionary<string, Album> _albumsByName;

    private LibraryLinks(FossegrimDbContext db, Dictionary<string, Artist> artists, Dictionary<string, Album> albums)
    {
        _db = db;
        _artistsByName = artists;
        _albumsByName = albums;
    }

    public static async Task<LibraryLinks> LoadAsync(FossegrimDbContext db, CancellationToken cancellationToken = default)
    {
        var artists = await db.Artists.ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        // Album artists must be loaded: the album-artist link is only added
        // when missing, and an unloaded collection would look empty and
        // cause a duplicate-row insert.
        var albums = await db.Albums.Include(a => a.Artists).ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        return new LibraryLinks(db, artists, albums);
    }

    /// <summary>
    /// Points the item's artist and album links at the ones its ArtistName
    /// and Album name, adding the album-artist link. Returns the artists and
    /// albums it was unlinked from, for <see cref="RemoveEmptyAsync"/>.
    /// </summary>
    public (IEnumerable<Guid> ArtistIds, IEnumerable<Guid> AlbumIds) Relink(MediaItem item)
    {
        var artist = ResolveArtist(item.ArtistName);
        var album = ResolveAlbum(item.Album);

        if (album is not null && artist is not null && !album.Artists.Contains(artist))
        {
            album.Artists.Add(artist);
        }

        var oldArtists = item.Artists.Where(a => a != artist).Select(a => a.Id).ToList();
        var oldAlbums = item.Albums.Where(a => a != album).Select(a => a.Id).ToList();

        if (oldArtists.Count > 0 || (artist is not null && !item.Artists.Contains(artist)))
        {
            item.Artists.Clear();
            if (artist is not null)
            {
                item.Artists.Add(artist);
            }
        }

        if (oldAlbums.Count > 0 || (album is not null && !item.Albums.Contains(album)))
        {
            item.Albums.Clear();
            if (album is not null)
            {
                item.Albums.Add(album);
            }
        }

        return (oldArtists, oldAlbums);
    }

    /// <summary>Removes those of the given artists and albums that no longer have tracks.</summary>
    public async Task RemoveEmptyAsync(IReadOnlyCollection<Guid> artistIds, IReadOnlyCollection<Guid> albumIds, CancellationToken cancellationToken = default)
    {
        if (artistIds.Count == 0 && albumIds.Count == 0)
        {
            return;
        }

        var emptyAlbums = await _db.Albums.Where(a => albumIds.Contains(a.Id) && !a.MediaItems.Any()).ToListAsync(cancellationToken);
        var emptyArtists = await _db.Artists.Where(a => artistIds.Contains(a.Id) && !a.MediaItems.Any()).ToListAsync(cancellationToken);

        foreach (var album in emptyAlbums)
        {
            _albumsByName.Remove(album.Name);
        }
        foreach (var artist in emptyArtists)
        {
            _artistsByName.Remove(artist.Name);
        }

        _db.Albums.RemoveRange(emptyAlbums);
        _db.Artists.RemoveRange(emptyArtists);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private Artist? ResolveArtist(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var name = artistName.Trim();
        if (!_artistsByName.TryGetValue(name, out var artist))
        {
            artist = new Artist { Id = Guid.NewGuid(), Name = name };
            _artistsByName[name] = artist;
            _db.Artists.Add(artist);
        }
        return artist;
    }

    private Album? ResolveAlbum(string? albumName)
    {
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return null;
        }

        var name = albumName.Trim();
        if (!_albumsByName.TryGetValue(name, out var album))
        {
            album = new Album { Id = Guid.NewGuid(), Name = name };
            _albumsByName[name] = album;
            _db.Albums.Add(album);
        }
        return album;
    }
}

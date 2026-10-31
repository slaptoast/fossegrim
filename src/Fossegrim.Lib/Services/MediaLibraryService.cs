using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Lib.Services;

public class MediaLibraryService
{
    private readonly FossegrimDbContext _db;

    public MediaLibraryService(FossegrimDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Writes ID3 tags to disk for each requested media item and mirrors the
    /// change into the database, re-resolving the Artist/Album associations
    /// the same way a fresh scan would. Only fields that differ from what's
    /// stored are written, so untouched tags in the file (including ones the
    /// database never read, or extra artists and genres) are left alone.
    /// Artists and albums an edit leaves with no tracks are removed. Items
    /// that fail (missing file, unreadable tag data, etc.) are reported as
    /// errors and don't affect the others.
    /// </summary>
    public async Task<UpdateMediaItemTagsResponse> UpdateTagsAsync(IEnumerable<MediaItemTagUpdate> updates)
    {
        var errors = new List<MediaItemTagUpdateError>();
        var updatedItems = new List<MediaItem>();
        // Artists/albums unlinked by an edit; removed at the end if now empty.
        var unlinkedArtistIds = new HashSet<Guid>();
        var unlinkedAlbumIds = new HashSet<Guid>();

        var artistsByName = await _db.Artists.ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase);
        // Artists must be loaded up front: the album-artist link is only added
        // below when missing, and an unloaded collection would look empty even
        // when the link already exists, causing a duplicate-row insert to fail.
        var albumsByName = await _db.Albums.Include(a => a.Artists).ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase);

        Artist? ResolveArtist(string? artistName)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            var name = artistName.Trim();
            if (artistsByName.TryGetValue(name, out var artist))
            {
                return artist;
            }

            artist = new Artist { Id = Guid.NewGuid(), Name = name };
            artistsByName[name] = artist;
            _db.Artists.Add(artist);
            return artist;
        }

        Album? ResolveAlbum(string? albumName)
        {
            if (string.IsNullOrWhiteSpace(albumName))
            {
                return null;
            }

            var name = albumName.Trim();
            if (albumsByName.TryGetValue(name, out var album))
            {
                return album;
            }

            album = new Album { Id = Guid.NewGuid(), Name = name };
            albumsByName[name] = album;
            _db.Albums.Add(album);
            return album;
        }

        foreach (var update in updates)
        {
            var item = await _db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .FirstOrDefaultAsync(m => m.Id == update.Id);

            if (item is null)
            {
                errors.Add(new MediaItemTagUpdateError(update.Id, "Media item not found."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.FileLocation) || !File.Exists(item.FileLocation))
            {
                errors.Add(new MediaItemTagUpdateError(update.Id, "File not found on disk."));
                continue;
            }

            var title = Normalize(update.Title);
            var artistName = Normalize(update.ArtistName);
            var albumName = Normalize(update.Album);
            var genre = Normalize(update.Genre);
            var track = update.Track is > 0 ? update.Track : null;
            var year = update.Year is > 0 ? update.Year : null;

            var titleChanged = title != Normalize(item.Title);
            var artistChanged = !string.Equals(artistName, Normalize(item.ArtistName), StringComparison.Ordinal);
            var albumChanged = !string.Equals(albumName, Normalize(item.Album), StringComparison.Ordinal);
            var genreChanged = genre != Normalize(item.Genre);
            var trackChanged = track != item.Track;
            var yearChanged = year != item.Year;

            if (!(titleChanged || artistChanged || albumChanged || genreChanged || trackChanged || yearChanged))
            {
                // Nothing to do; don't rewrite the file.
                updatedItems.Add(item);
                continue;
            }

            try
            {
                using var tagFile = TagLib.File.Create(item.FileLocation);
                if (titleChanged)
                {
                    tagFile.Tag.Title = title;
                }
                if (artistChanged)
                {
                    tagFile.Tag.Performers = artistName is null ? [] : [artistName];
                }
                if (albumChanged)
                {
                    tagFile.Tag.Album = albumName;
                }
                if (genreChanged)
                {
                    tagFile.Tag.Genres = genre is null ? [] : [genre];
                }
                if (trackChanged)
                {
                    tagFile.Tag.Track = (uint)(track ?? 0);
                }
                if (yearChanged)
                {
                    tagFile.Tag.Year = (uint)(year ?? 0);
                }
                tagFile.Save();
                // Writing through Tag updates every tag the file has, which
                // can bring ID3v1 back in line with ID3v2.
                item.TagVersionMismatch = TagReader.CompareId3Versions(tagFile);
            }
            catch (Exception ex)
            {
                errors.Add(new MediaItemTagUpdateError(update.Id, $"Failed to write tags to file: {ex.Message}"));
                continue;
            }

            RecordFileState(item);
            item.Title = title;
            item.ArtistName = artistName;
            item.Album = albumName;
            item.Track = track;
            item.Year = year;
            item.Genre = genre;
            item.LastModified = DateTime.UtcNow;

            // Re-link only when the artist or album changed, so links a scan
            // made are kept for an edit that didn't touch them.
            if (artistChanged || albumChanged)
            {
                var artist = ResolveArtist(artistName);
                var album = ResolveAlbum(albumName);

                if (album is not null && artist is not null && !album.Artists.Contains(artist))
                {
                    album.Artists.Add(artist);
                }

                if (artistChanged)
                {
                    unlinkedArtistIds.UnionWith(item.Artists.Select(a => a.Id));
                    item.Artists.Clear();
                    if (artist is not null)
                    {
                        item.Artists.Add(artist);
                    }
                }

                if (albumChanged)
                {
                    unlinkedAlbumIds.UnionWith(item.Albums.Select(a => a.Id));
                    item.Albums.Clear();
                    if (album is not null)
                    {
                        item.Albums.Add(album);
                    }
                }
            }

            updatedItems.Add(item);
        }

        await _db.SaveChangesAsync();
        await RemoveEmptyArtistsAndAlbumsAsync(unlinkedArtistIds, unlinkedAlbumIds);

        var updatedDtos = updatedItems.Select(m => new MediaItemDto(
            m.Id,
            m.Title,
            m.ArtistName,
            m.Album,
            m.DateAdded,
            m.LastModified,
            m.Year,
            m.Track,
            m.Genre,
            m.Duration,
            m.Bitrate,
            m.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name)),
            m.Albums.Select(a => new AlbumSummaryDto(a.Id, a.Name, a.Year))));

        return new UpdateMediaItemTagsResponse(updatedDtos, errors);
    }

    // The app just rewrote the file; record it as read so the next quick
    // scan doesn't re-read it for nothing.
    private static void RecordFileState(MediaItem item)
    {
        var info = new FileInfo(item.FileLocation!);
        item.FileSize = info.Length;
        item.FileModifiedAt = info.LastWriteTimeUtc;
    }

    // Blank or whitespace-only means "no value", stored as null.
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Called after an edit; only checks the artists/albums that edit unlinked,
    // so it never sweeps the whole library.
    private async Task RemoveEmptyArtistsAndAlbumsAsync(IReadOnlyCollection<Guid> artistIds, IReadOnlyCollection<Guid> albumIds)
    {
        if (artistIds.Count == 0 && albumIds.Count == 0)
        {
            return;
        }

        var emptyAlbums = await _db.Albums
            .Where(a => albumIds.Contains(a.Id) && !a.MediaItems.Any())
            .ToListAsync();
        var emptyArtists = await _db.Artists
            .Where(a => artistIds.Contains(a.Id) && !a.MediaItems.Any())
            .ToListAsync();

        _db.Albums.RemoveRange(emptyAlbums);
        _db.Artists.RemoveRange(emptyArtists);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Extracts album art, preferring embedded metadata (from the first
    /// playable file found, ordered by track number then file path) and
    /// falling back to a cover.jpg/cover.png file sitting next to the audio
    /// files if none of the tracks have embedded art.
    /// </summary>
    public async Task<(byte[] Data, string ContentType)?> GetAlbumCoverAsync(Guid albumId)
    {
        var fileLocations = await _db.Albums
            .Where(a => a.Id == albumId)
            .SelectMany(a => a.MediaItems)
            .OrderBy(m => m.Track ?? int.MaxValue)
            .ThenBy(m => m.FileLocation)
            .Select(m => m.FileLocation)
            .ToListAsync();

        var validFiles = fileLocations
            .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
            .ToList();

        foreach (var fileLocation in validFiles)
        {
            try
            {
                using var tagFile = TagLib.File.Create(fileLocation);
                // Files can hold several pictures (back cover, artist...); the front cover is the album's.
                var picture = tagFile.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                    ?? tagFile.Tag.Pictures.FirstOrDefault();
                if (picture is not null)
                {
                    var contentType = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
                    return (picture.Data.ToArray(), contentType);
                }
            }
            catch
            {
                // Try the next file in the album.
            }
        }

        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileLocation in validFiles)
        {
            var directory = Path.GetDirectoryName(fileLocation);
            if (directory is null || !checkedDirectories.Add(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            var coverFile = Directory.EnumerateFiles(directory).FirstOrDefault(IsCoverFileName);
            if (coverFile is not null)
            {
                var contentType = Path.GetExtension(coverFile).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "image/jpeg";
                return (await File.ReadAllBytesAsync(coverFile), contentType);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes <paramref name="data"/> as the front cover of every track in the
    /// album, replacing any front cover and keeping the files' other pictures.
    /// Returns null when the album doesn't exist. Tracks that fail (missing
    /// file, unwritable tags) are reported and don't stop the others.
    /// </summary>
    public async Task<SetAlbumCoverResponse?> SetAlbumCoverAsync(Guid albumId, byte[] data, string mimeType)
    {
        var album = await _db.Albums
            .Include(a => a.MediaItems)
            .FirstOrDefaultAsync(a => a.Id == albumId);
        if (album is null)
        {
            return null;
        }

        var errors = new List<MediaItemTagUpdateError>();
        var updated = 0;
        foreach (var item in album.MediaItems)
        {
            if (string.IsNullOrWhiteSpace(item.FileLocation) || !File.Exists(item.FileLocation))
            {
                errors.Add(new MediaItemTagUpdateError(item.Id, "File not found on disk."));
                continue;
            }

            try
            {
                using var tagFile = TagLib.File.Create(item.FileLocation);
                var cover = new TagLib.Picture(new TagLib.ByteVector(data))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType,
                    Description = "Front cover",
                };
                tagFile.Tag.Pictures = tagFile.Tag.Pictures
                    .Where(p => p.Type != TagLib.PictureType.FrontCover)
                    .Prepend(cover)
                    .ToArray();
                tagFile.Save();
            }
            catch (Exception ex)
            {
                errors.Add(new MediaItemTagUpdateError(item.Id, $"Failed to write the cover to the file: {ex.Message}"));
                continue;
            }

            RecordFileState(item);
            item.LastModified = DateTime.UtcNow;
            updated++;
        }

        if (updated > 0)
        {
            album.LastModified = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return new SetAlbumCoverResponse(updated, errors);
    }

    private static bool IsCoverFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        return string.Equals(name, "cover", StringComparison.OrdinalIgnoreCase)
            && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
    }
}

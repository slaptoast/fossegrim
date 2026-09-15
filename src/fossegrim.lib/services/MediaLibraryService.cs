using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Lib.Services;

public class MediaLibraryService
{
    private readonly FossegrimDbContext _db;

    public MediaLibraryService(FossegrimDbContext db)
    {
        _db = db;
    }

    public async Task<ScanResult> ScanAndSaveMediaItemsAsync(string folderPath, int maxDegreeOfParallelism = 4)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("FolderPath cannot be null or empty", nameof(folderPath));
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
        }

        // Find the MediaFolder entity for this path
        var mediaFolder = await _db.MediaFolders
            .FirstOrDefaultAsync(f => f.Location == folderPath);

        // Update the last scanned date if folder exists
        if (mediaFolder != null)
        {
            mediaFolder.DateLastScanned = DateTime.UtcNow;
        }

        var scanner = new FileScanner(maxDegreeOfParallelism: maxDegreeOfParallelism);
        var scannedItems = await scanner.ScanFilesAsync(folderPath);

        var itemsList = scannedItems.ToList();
        var itemsAdded = 0;
        var itemsUpdated = 0;

        var artistsByName = await _db.Artists.ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var albumsByName = await _db.Albums.ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase);

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

        foreach (var scannedItem in itemsList)
        {
            // Check if item already exists by FileLocation
            var existingItem = await _db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .FirstOrDefaultAsync(m => m.FileLocation == scannedItem.FileLocation);

            var artist = ResolveArtist(scannedItem.ArtistName);
            var album = ResolveAlbum(scannedItem.Album);

            if (album is not null && artist is not null && !album.Artists.Contains(artist))
            {
                album.Artists.Add(artist);
            }

            if (existingItem == null)
            {
                // Create new MediaItem
                var newItem = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    Title = scannedItem.Title,
                    ArtistName = scannedItem.ArtistName,
                    Album = scannedItem.Album,
                    FileLocation = scannedItem.FileLocation,
                    Year = scannedItem.Year,
                    Track = scannedItem.Track,
                    Genre = scannedItem.Genre,
                    Duration = scannedItem.Duration,
                    Bitrate = scannedItem.Bitrate,
                    MediaFolderId = mediaFolder?.Id
                };

                if (artist is not null)
                {
                    newItem.Artists.Add(artist);
                }

                if (album is not null)
                {
                    newItem.Albums.Add(album);
                }

                _db.MediaItems.Add(newItem);
                itemsAdded++;
            }
            else
            {
                // Update existing item
                existingItem.Title = scannedItem.Title;
                existingItem.ArtistName = scannedItem.ArtistName;
                existingItem.Album = scannedItem.Album;
                existingItem.Year = scannedItem.Year;
                existingItem.Track = scannedItem.Track;
                existingItem.Genre = scannedItem.Genre;
                existingItem.Duration = scannedItem.Duration;
                existingItem.Bitrate = scannedItem.Bitrate;
                existingItem.LastModified = DateTime.UtcNow;
                existingItem.MediaFolderId = mediaFolder?.Id;

                if (artist is not null && !existingItem.Artists.Contains(artist))
                {
                    existingItem.Artists.Add(artist);
                }

                if (album is not null && !existingItem.Albums.Contains(album))
                {
                    existingItem.Albums.Add(album);
                }

                itemsUpdated++;
            }
        }

        await _db.SaveChangesAsync();

        return new ScanResult
        {
            Success = true,
            ScannedCount = itemsList.Count,
            ItemsAdded = itemsAdded,
            ItemsUpdated = itemsUpdated,
            FolderPath = folderPath
        };
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
                var picture = tagFile.Tag.Pictures.FirstOrDefault();
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

    private static bool IsCoverFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        return string.Equals(name, "cover", StringComparison.OrdinalIgnoreCase)
            && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
    }
}

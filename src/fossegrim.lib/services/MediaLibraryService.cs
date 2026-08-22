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

        foreach (var scannedItem in itemsList)
        {
            // Check if item already exists by FileLocation
            var existingItem = await _db.MediaItems
                .FirstOrDefaultAsync(m => m.FileLocation == scannedItem.FileLocation);

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
}

public class ScanResult
{
    public bool Success { get; set; }
    public int ScannedCount { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public string FolderPath { get; set; } = string.Empty;
}

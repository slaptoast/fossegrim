using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Turns "scan these folders / this path / this album" into a
/// <see cref="ScanJobSpec"/>. Every path a scan touches is inside a
/// configured media folder.
/// </summary>
public class ScanRequests
{
    private readonly FossegrimDbContext _db;

    public ScanRequests(FossegrimDbContext db)
    {
        _db = db;
    }

    /// <summary>The spec, or an error message for the caller.</summary>
    public async Task<(ScanJobSpec? Spec, string? Error)> ForFoldersOrPathAsync(IReadOnlyList<Guid>? folderIds, string? path, bool full, string source)
    {
        var folders = await _db.MediaFolders.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
        if (folders.Count == 0)
        {
            return (null, "No media folders are configured yet. Add one in Settings first.");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (null, $"'{path}' isn't a valid path.");
            }

            // A path naming a file scans the folder it's in (just that folder).
            var target = File.Exists(fullPath)
                ? new ScanTarget(Path.GetDirectoryName(fullPath)!, Recursive: false)
                : new ScanTarget(fullPath);

            var folder = folders.FirstOrDefault(f => Normalize(f.Location) == target.Path)
                ?? LibraryScanner.FolderFor(target.Path + Path.DirectorySeparatorChar + "_", folders);
            if (folder is null)
            {
                return (null, $"'{path}' isn't inside a configured media folder.");
            }

            return (new ScanJobSpec([target], full, target.Path, source), null);
        }

        var chosen = folderIds is { Count: > 0 }
            ? folders.Where(f => folderIds.Contains(f.Id)).ToList()
            : folders;
        if (chosen.Count == 0)
        {
            return (null, "None of those media folders exist.");
        }

        var description = chosen.Count == folders.Count && folderIds is not { Count: > 0 }
            ? "All media folders"
            : string.Join(", ", chosen.Select(f => f.Name));
        return (new ScanJobSpec(chosen.Select(f => new ScanTarget(Normalize(f.Location))).ToList(), full, description, source), null);
    }

    /// <summary>A full rescan of every folder holding the album's tracks, or null if it doesn't exist.</summary>
    public async Task<ScanJobSpec?> ForAlbumAsync(Guid albumId)
    {
        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => new { a.Name, Paths = a.MediaItems.Select(m => m.FileLocation).ToList() })
            .FirstOrDefaultAsync();
        return album is null ? null : ForTrackFolders(album.Paths, $"Album: {album.Name}");
    }

    /// <summary>A full rescan of every folder holding the artist's tracks, or null if it doesn't exist.</summary>
    public async Task<ScanJobSpec?> ForArtistAsync(Guid artistId)
    {
        var artist = await _db.Artists.AsNoTracking()
            .Where(a => a.Id == artistId)
            .Select(a => new { a.Name, Paths = a.MediaItems.Select(m => m.FileLocation).ToList() })
            .FirstOrDefaultAsync();
        return artist is null ? null : ForTrackFolders(artist.Paths, $"Artist: {artist.Name}");
    }

    // Each folder on its own, not recursively: an album's folder is rescanned
    // without sweeping up everything below it.
    private static ScanJobSpec ForTrackFolders(IEnumerable<string?> paths, string description)
    {
        var targets = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetDirectoryName(p!)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(d => new ScanTarget(Normalize(d), Recursive: false))
            .ToList();
        return new ScanJobSpec(targets, Full: true, description, "rescan");
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

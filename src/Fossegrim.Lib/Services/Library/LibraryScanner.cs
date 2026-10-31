using System.Collections.Concurrent;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fossegrim.Lib.Services.Library;

/// <summary>A folder to scan; Recursive = false scans only the files directly in it.</summary>
public record ScanTarget(string Path, bool Recursive = true);

public record ScanProgress(ScanPhase Phase, int Processed, int Total);

/// <summary>
/// Brings the library in line with what's on disk under the given folders.
/// It counts the audio files first (so progress has a total), then reads
/// tags. A quick scan reads only new files and ones whose size or
/// last-write time changed; a full scan reads them all. Tracks whose file
/// is gone are removed, except that a file that reappears elsewhere with
/// the same size and tags is treated as moved (keeping its history and
/// playlist entries), and nothing is removed from a folder that's missing
/// or has no audio files at all, which usually means an unmounted drive.
/// </summary>
public class LibraryScanner
{
    private const int MaxWarnings = 50;
    private const int BatchSize = 500;

    private readonly FossegrimDbContext _db;
    private readonly ILogger<LibraryScanner> _logger;

    public LibraryScanner(FossegrimDbContext db, ILogger<LibraryScanner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public int MaxDegreeOfParallelism { get; set; } = 4;

    public async Task<ScanSummary> ScanAsync(
        IReadOnlyList<ScanTarget> targets,
        bool full,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        void Warn(string message)
        {
            _logger.LogWarning("Scan: {Message}", message);
            lock (warnings)
            {
                if (warnings.Count < MaxWarnings)
                {
                    warnings.Add(message);
                }
            }
        }

        targets = targets.Select(t => t with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(t.Path)) }).ToList();

        // 1. Count: find every audio file under the targets.
        progress?.Report(new ScanProgress(ScanPhase.Counting, 0, 0));
        var files = new HashSet<string>(StringComparer.Ordinal);
        var unreachable = new List<ScanTarget>();
        foreach (var target in targets)
        {
            if (!Directory.Exists(target.Path))
            {
                Warn($"Folder not found: {target.Path}. Nothing was removed from it.");
                unreachable.Add(target);
                continue;
            }

            var found = Directory.EnumerateFiles(target.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = target.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Where(AudioFormats.IsSupported).ToList();

            if (found.Count == 0)
            {
                // Removing everything here would be right if the folder were
                // really emptied, but disastrous if a drive isn't mounted.
                unreachable.Add(target);
            }
            files.UnionWith(found);
            progress?.Report(new ScanProgress(ScanPhase.Counting, 0, files.Count));
        }

        // What the library already has under the targets.
        var existing = (await _db.MediaItems
                .AsNoTracking()
                .Where(m => m.FileLocation != null)
                .Select(m => new ExistingItem(m.Id, m.FileLocation!, m.FileSize, m.FileModifiedAt, m.Title, m.ArtistName, m.Album))
                .ToListAsync(cancellationToken))
            .Where(m => targets.Any(t => IsUnder(m.Path, t)))
            .ToDictionary(m => m.Path, StringComparer.Ordinal);

        foreach (var target in unreachable.Where(t => Directory.Exists(t.Path)))
        {
            var kept = existing.Values.Count(m => IsUnder(m.Path, target));
            if (kept > 0)
            {
                Warn($"No audio files found in {target.Path}, so its {kept} track{(kept == 1 ? "" : "s")} were kept in case the drive isn't mounted.");
            }
        }

        // 2. Read: tags from new and changed files (all files on a full scan).
        var total = files.Count;
        var processed = 0;
        var unchanged = 0;
        var failed = 0;
        var read = new ConcurrentBag<ScannedFile>();
        progress?.Report(new ScanProgress(ScanPhase.Reading, 0, total));

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken }, (path, _) =>
        {
            try
            {
                var info = new FileInfo(path);
                var size = info.Length;
                var modifiedAt = info.LastWriteTimeUtc;

                if (!full && existing.TryGetValue(path, out var known) && known.FileSize == size && known.FileModifiedAt == modifiedAt)
                {
                    Interlocked.Increment(ref unchanged);
                }
                else
                {
                    read.Add(new ScannedFile(path, size, modifiedAt, TagReader.Read(path)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                Warn($"Couldn't read {path}: {ex.Message}");
            }

            var done = Interlocked.Increment(ref processed);
            progress?.Report(new ScanProgress(ScanPhase.Reading, done, total));
            return ValueTask.CompletedTask;
        });

        // 3. Save.
        progress?.Report(new ScanProgress(ScanPhase.Saving, total, total));
        var folders = await _db.MediaFolders.ToListAsync(cancellationToken);
        var links = await LibraryLinks.LoadAsync(_db, cancellationToken);
        var unlinkedArtists = new HashSet<Guid>();
        var unlinkedAlbums = new HashSet<Guid>();

        var missing = existing.Values
            .Where(m => !files.Contains(m.Path) && !unreachable.Any(t => IsUnder(m.Path, t)))
            .ToList();

        var changed = read.Where(f => existing.ContainsKey(f.Path)).ToList();
        var added = read.Where(f => !existing.ContainsKey(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // A new file matching a missing one by size and tags is that file, moved.
        var moves = new Dictionary<Guid, ScannedFile>();
        foreach (var file in added.ToList())
        {
            var match = missing.FirstOrDefault(m =>
                m.FileSize == file.Size &&
                SameText(m.Title, file.Tags.Title) &&
                SameText(m.ArtistName, file.Tags.ArtistName) &&
                SameText(m.Album, file.Tags.Album));
            if (match is not null)
            {
                moves[match.Id] = file;
                missing.Remove(match);
                added.Remove(file);
            }
        }

        var updates = changed.ToDictionary(f => existing[f.Path].Id);
        foreach (var (id, file) in moves)
        {
            updates[id] = file;
        }

        foreach (var batch in updates.Keys.Chunk(BatchSize))
        {
            var items = await _db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .Where(m => batch.Contains(m.Id))
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                Apply(item, updates[item.Id], folders);
                var (artists, albums) = links.Relink(item);
                unlinkedArtists.UnionWith(artists);
                unlinkedAlbums.UnionWith(albums);
            }
            await _db.SaveChangesAsync(cancellationToken);
        }

        foreach (var file in added)
        {
            var item = new MediaItem { Id = Guid.NewGuid(), DateAdded = DateTime.UtcNow };
            Apply(item, file, folders);
            links.Relink(item);
            _db.MediaItems.Add(item);
        }
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var batch in missing.Select(m => m.Id).Chunk(BatchSize))
        {
            var items = await _db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .Where(m => batch.Contains(m.Id))
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                unlinkedArtists.UnionWith(item.Artists.Select(a => a.Id));
                unlinkedAlbums.UnionWith(item.Albums.Select(a => a.Id));
            }
            _db.MediaItems.RemoveRange(items);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await links.RemoveEmptyAsync(unlinkedArtists, unlinkedAlbums, cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var folder in folders.Where(f => targets.Any(t => t.Recursive && PathsEqual(t.Path, f.Location))))
        {
            folder.DateLastScanned = now;
        }
        await _db.SaveChangesAsync(cancellationToken);

        progress?.Report(new ScanProgress(ScanPhase.Done, total, total));
        return new ScanSummary(total, added.Count, changed.Count, unchanged, moves.Count, missing.Count, failed, warnings);
    }

    private static void Apply(MediaItem item, ScannedFile file, IReadOnlyList<MediaFolder> folders)
    {
        item.FileLocation = file.Path;
        item.FileSize = file.Size;
        item.FileModifiedAt = file.ModifiedAt;
        item.Title = file.Tags.Title;
        item.ArtistName = file.Tags.ArtistName;
        item.Album = file.Tags.Album;
        item.Year = file.Tags.Year;
        item.Track = file.Tags.Track;
        item.Genre = file.Tags.Genre;
        item.Duration = file.Tags.Duration;
        item.Bitrate = file.Tags.Bitrate;
        item.TagVersionMismatch = file.Tags.TagVersionMismatch;
        item.LastModified = DateTime.UtcNow;
        item.MediaFolderId = FolderFor(file.Path, folders)?.Id;
    }

    /// <summary>The configured media folder a file is in (the deepest, if they nest).</summary>
    public static MediaFolder? FolderFor(string path, IEnumerable<MediaFolder> folders) =>
        folders
            .Where(f => IsUnder(path, new ScanTarget(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Location)))))
            .MaxBy(f => f.Location.Length);

    public static bool IsUnder(string path, ScanTarget target) => target.Recursive
        ? path.StartsWith(target.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        : string.Equals(Path.GetDirectoryName(path), target.Path, StringComparison.Ordinal);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);

    private static bool SameText(string? a, string? b) =>
        string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private record ExistingItem(Guid Id, string Path, long? FileSize, DateTime? FileModifiedAt, string? Title, string? ArtistName, string? Album);

    private record ScannedFile(string Path, long Size, DateTime ModifiedAt, AudioFileTags Tags);
}

using System.Diagnostics;
using System.Threading.Channels;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fossegrim.Lib.Services.Library;

/// <summary>A folder to scan; Recursive = false scans only the files directly in it.</summary>
public record ScanTarget(string Path, bool Recursive = true);

/// <summary>Saved: how many of the read files are in the library so far.</summary>
public record ScanProgress(ScanPhase Phase, int Processed, int Total, int Saved = 0);

/// <summary>
/// Brings the library in line with what's on disk under the given folders.
/// It counts the audio files first (so progress has a total), then reads
/// tags, saving in small batches as it goes so new music shows up in the
/// library before the scan finishes. A quick scan reads only new files and ones whose size or
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
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SearchRebuildInterval = TimeSpan.FromSeconds(60);

    private readonly FossegrimDbContext _db;
    private readonly ILogger<LibraryScanner> _logger;

    public LibraryScanner(FossegrimDbContext db, ILogger<LibraryScanner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>How many read files are saved at once while scanning (or fewer, every 2 seconds).</summary>
    public int SaveBatchSize { get; set; } = 200;

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

        // 2. Read and save: tags from new and changed files (all files on a
        // full scan), saved in small batches as they're read so the music
        // shows up in the library while the scan runs. Files are read in path
        // order, so a batch is mostly whole albums.
        var folders = await _db.MediaFolders.ToListAsync(cancellationToken);
        var links = await LibraryLinks.LoadAsync(_db, cancellationToken);
        var unlinkedArtists = new HashSet<Guid>();
        var unlinkedAlbums = new HashSet<Guid>();

        // Known up front: tracks whose file wasn't found. A new file matching
        // one of these by size and tags is that file, moved.
        var missing = existing.Values
            .Where(m => !files.Contains(m.Path) && !unreachable.Any(t => IsUnder(m.Path, t)))
            .ToList();

        var total = files.Count;
        var processed = 0;
        var unchanged = 0;
        var failed = 0;
        var saved = 0;
        var added = 0;
        var changed = 0;
        var moved = 0;
        void ReportReading() =>
            progress?.Report(new ScanProgress(ScanPhase.Reading, Volatile.Read(ref processed), total, Volatile.Read(ref saved)));
        ReportReading();

        var channel = Channel.CreateBounded<ScannedFile>(new BoundedChannelOptions(SaveBatchSize * 2) { SingleReader = true });

        // If saving fails, stop reading: the readers would otherwise wait on a full channel.
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task SaveAllAsync()
        {
            var batch = new List<ScannedFile>();
            var batchAge = new Stopwatch();
            var sinceIndex = Stopwatch.StartNew();
            var reader = channel.Reader;
            Task<bool>? readable = null;

            async Task FlushAsync()
            {
                if (batch.Count == 0)
                {
                    return;
                }
                var (a, c, m) = await SaveBatchAsync(batch, existing, missing, folders, links, unlinkedArtists, unlinkedAlbums, cancellationToken);
                (added, changed, moved) = (added + a, changed + c, moved + m);
                Interlocked.Add(ref saved, batch.Count);
                batch.Clear();
                ReportReading();

                // Keep search roughly in step on long scans; it's rebuilt again at the end.
                if (sinceIndex.Elapsed >= SearchRebuildInterval)
                {
                    await new SearchIndex(_db).RebuildAsync(cancellationToken);
                    sinceIndex.Restart();
                }
            }

            try
            {
                while (true)
                {
                    readable ??= reader.WaitToReadAsync(cancellationToken).AsTask();
                    if (batch.Count > 0)
                    {
                        // Save a part-filled batch once it's SaveInterval old, so a slow trickle still shows up.
                        var left = SaveInterval - batchAge.Elapsed;
                        if (left > TimeSpan.Zero)
                        {
                            await Task.WhenAny(readable, Task.Delay(left, cancellationToken));
                        }
                    }
                    else
                    {
                        await Task.WhenAny(readable);
                    }

                    if (readable.IsCompleted)
                    {
                        var more = await readable;
                        readable = null;
                        if (!more)
                        {
                            break;
                        }
                        while (batch.Count < SaveBatchSize && reader.TryRead(out var file))
                        {
                            if (batch.Count == 0)
                            {
                                batchAge.Restart();
                            }
                            batch.Add(file);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (batch.Count >= SaveBatchSize || (batch.Count > 0 && batchAge.Elapsed >= SaveInterval))
                    {
                        await FlushAsync();
                    }
                }
                await FlushAsync();
            }
            catch
            {
                readCancellation.Cancel();
                throw;
            }
        }

        var saving = SaveAllAsync();
        var ordered = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        try
        {
            await Parallel.ForEachAsync(ordered, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = readCancellation.Token }, async (path, token) =>
            {
                ScannedFile? scanned = null;
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
                        scanned = new ScannedFile(path, size, modifiedAt, TagReader.Read(path));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    Warn($"Couldn't read {path}: {ex.Message}");
                }

                if (scanned is not null)
                {
                    await channel.Writer.WriteAsync(scanned, token);
                }
                Interlocked.Increment(ref processed);
                ReportReading();
            });
        }
        catch
        {
            // Whatever was read is still saved (unless that's what failed),
            // so a later quick scan carries on from there.
            channel.Writer.TryComplete();
            try
            {
                await saving;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
            throw;
        }
        channel.Writer.Complete();
        await saving;

        // 3. Tidy up: remove what's gone, refresh search.
        progress?.Report(new ScanProgress(ScanPhase.Saving, total, total, saved));

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

        // Tracks, albums and artists may all have changed.
        await new SearchIndex(_db).RebuildAsync(cancellationToken);

        progress?.Report(new ScanProgress(ScanPhase.Done, total, total, saved));
        return new ScanSummary(total, added, changed, unchanged, moved, missing.Count, failed, warnings);
    }

    /// <summary>
    /// Saves one batch of read files: updates for known paths, moves for new
    /// paths matching a missing track (removed from <paramref name="missing"/>),
    /// and new tracks for the rest. Returns how many of each.
    /// </summary>
    private async Task<(int Added, int Changed, int Moved)> SaveBatchAsync(
        IReadOnlyList<ScannedFile> batch,
        IReadOnlyDictionary<string, ExistingItem> existing,
        List<ExistingItem> missing,
        IReadOnlyList<MediaFolder> folders,
        LibraryLinks links,
        HashSet<Guid> unlinkedArtists,
        HashSet<Guid> unlinkedAlbums,
        CancellationToken cancellationToken)
    {
        var updates = new Dictionary<Guid, ScannedFile>();
        var added = new List<ScannedFile>();
        var (changed, moved) = (0, 0);
        foreach (var file in batch)
        {
            if (existing.TryGetValue(file.Path, out var known))
            {
                updates[known.Id] = file;
                changed++;
                continue;
            }

            var match = missing.FirstOrDefault(m =>
                m.FileSize == file.Size &&
                SameText(m.Title, file.Tags.Title) &&
                SameText(m.ArtistName, file.Tags.ArtistName) &&
                SameText(m.Album, file.Tags.Album));
            if (match is not null)
            {
                missing.Remove(match);
                updates[match.Id] = file;
                moved++;
            }
            else
            {
                added.Add(file);
            }
        }

        if (updates.Count > 0)
        {
            var ids = updates.Keys.ToList();
            var items = await _db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .Where(m => ids.Contains(m.Id))
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                Apply(item, updates[item.Id], folders);
                var (artists, albums) = links.Relink(item);
                unlinkedArtists.UnionWith(artists);
                unlinkedAlbums.UnionWith(albums);
            }
        }

        foreach (var file in added)
        {
            var item = new MediaItem { Id = Guid.NewGuid(), DateAdded = DateTime.UtcNow };
            Apply(item, file, folders);
            links.Relink(item);
            _db.MediaItems.Add(item);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (added.Count, changed, moved);
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
        item.Disc = file.Tags.Disc;
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

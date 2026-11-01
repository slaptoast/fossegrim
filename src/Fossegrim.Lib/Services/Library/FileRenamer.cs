using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fossegrim.Lib.Services.Library;

/// <summary>
/// Renames and moves audio files to match the server's file-name template,
/// relative to the media folder each file is in. <see cref="PlanAsync"/>
/// says what would happen; <see cref="ApplyAsync"/> does it. A file is never
/// overwritten. When every track leaves a folder for the same new folder,
/// the folder's other files (cover.jpg and the like) go with them; folders
/// left empty are removed, up to the media folder itself.
/// </summary>
public class FileRenamer
{
    public const string TemplateSetting = "Rename:Template";

    private static readonly FileNameTags SampleTags = new("Massive Attack", "Mezzanine", "Teardrop", 3, 1998, "Trip-Hop");

    private readonly FossegrimDbContext _db;
    private readonly ILogger<FileRenamer> _logger;

    public FileRenamer(FossegrimDbContext db, ILogger<FileRenamer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string> GetTemplateAsync() =>
        (await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == TemplateSetting))?.Value is { Length: > 0 } template
            ? template
            : FileNameTemplate.Default;

    public static string Example(string template) => FileNameTemplate.Render(template, SampleTags, ".mp3");

    public async Task SetTemplateAsync(string template)
    {
        var setting = await _db.AppSettings.FindAsync(TemplateSetting);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = TemplateSetting, Value = template.Trim() });
        }
        else
        {
            setting.Value = template.Trim();
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>What renaming these tracks (null = the whole library) would do. Unchanged tracks are only counted.</summary>
    public async Task<RenamePlanDto> PlanAsync(IReadOnlyCollection<Guid>? mediaItemIds)
    {
        var template = await GetTemplateAsync();
        var items = await LoadAsync(mediaItemIds, tracking: false);
        var plan = await PlanAsync(items, template);
        return new RenamePlanDto(template,
            plan.Count(e => e.Status == RenameStatus.Unchanged),
            plan.Where(e => e.Status != RenameStatus.Unchanged).ToList());
    }

    public async Task<RenameResultDto> ApplyAsync(IReadOnlyCollection<Guid>? mediaItemIds)
    {
        var template = await GetTemplateAsync();
        var items = await LoadAsync(mediaItemIds, tracking: true);
        var byId = items.ToDictionary(i => i.Id);
        var plan = await PlanAsync(items, template);
        var folders = await _db.MediaFolders.AsNoTracking().ToListAsync();

        var problems = plan.Where(e => e.Status is not (RenameStatus.Unchanged or RenameStatus.Rename)).ToList();
        // Source folder -> the folders its tracks went to.
        var movedFrom = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var renamed = 0;

        foreach (var entry in plan.Where(e => e.Status == RenameStatus.Rename))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.NewPath!)!);
                if (string.Equals(entry.CurrentPath, entry.NewPath, StringComparison.OrdinalIgnoreCase))
                {
                    // A case-only rename: go via a temporary name, for case-insensitive file systems.
                    var temporary = entry.NewPath + ".renaming";
                    File.Move(entry.CurrentPath, temporary);
                    File.Move(temporary, entry.NewPath!);
                }
                else
                {
                    File.Move(entry.CurrentPath, entry.NewPath!, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(entry with { Status = RenameStatus.Failed, Message = ex.Message });
                continue;
            }

            byId[entry.MediaItemId].FileLocation = entry.NewPath;
            renamed++;

            var from = Path.GetDirectoryName(entry.CurrentPath)!;
            if (!movedFrom.TryGetValue(from, out var destinations))
            {
                movedFrom[from] = destinations = new HashSet<string>(StringComparer.Ordinal);
            }
            destinations.Add(Path.GetDirectoryName(entry.NewPath!)!);
        }

        await _db.SaveChangesAsync();

        foreach (var (from, destinations) in movedFrom)
        {
            TidyUp(from, destinations, folders);
        }

        return new RenameResultDto(renamed, plan.Count(e => e.Status == RenameStatus.Unchanged), problems);
    }

    private async Task<List<MediaItem>> LoadAsync(IReadOnlyCollection<Guid>? ids, bool tracking)
    {
        var query = tracking ? _db.MediaItems : _db.MediaItems.AsNoTracking();
        if (ids is not null)
        {
            query = query.Where(m => ids.Contains(m.Id));
        }
        return await query.Where(m => m.FileLocation != null).ToListAsync();
    }

    private async Task<List<RenameEntryDto>> PlanAsync(IReadOnlyList<MediaItem> items, string template)
    {
        var folders = await _db.MediaFolders.AsNoTracking().ToListAsync();
        var entries = items
            .OrderBy(i => i.FileLocation, StringComparer.Ordinal)
            .Select(item => Entry(item, template, folders))
            .ToList();

        // Two tracks can't both take the same name. Compared ignoring case,
        // since that's how macOS and Windows compare file names.
        var renaming = entries.Where(e => e.Status == RenameStatus.Rename).ToList();
        var taken = entries.Where(e => e.Status != RenameStatus.Rename).Select(e => e.CurrentPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in renaming.GroupBy(e => e.NewPath!, StringComparer.OrdinalIgnoreCase))
        {
            var clash = group.Count() > 1 || taken.Contains(group.Key);
            foreach (var entry in group)
            {
                var onDisk = !string.Equals(entry.CurrentPath, entry.NewPath, StringComparison.OrdinalIgnoreCase) && File.Exists(entry.NewPath);
                if (clash || onDisk)
                {
                    entries[entries.IndexOf(entry)] = entry with
                    {
                        Status = RenameStatus.Conflict,
                        Message = clash ? "Another track would get the same name." : "A file with that name already exists.",
                    };
                }
            }
        }

        return entries;
    }

    private static RenameEntryDto Entry(MediaItem item, string template, IReadOnlyList<MediaFolder> folders)
    {
        var path = item.FileLocation!;
        var folder = LibraryScanner.FolderFor(path, folders);
        var folderPath = folder is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Location));
        RenameEntryDto Make(string? newPath, RenameStatus status, string? message = null) =>
            new(item.Id, item.Title, item.ArtistName, item.Album, path, newPath, status, message, folderPath);

        if (folder is null)
        {
            return Make(null, RenameStatus.NotInMediaFolder, "Not inside a configured media folder.");
        }
        if (!File.Exists(path))
        {
            return Make(null, RenameStatus.FileMissing, "The file isn't on disk; rescan to remove it.");
        }

        var relative = FileNameTemplate.Render(
            template,
            new FileNameTags(item.ArtistName, item.Album, item.Title, item.Track, item.Year, item.Genre),
            Path.GetExtension(path));
        var newPath = Path.Combine(folderPath!, relative.Replace('/', Path.DirectorySeparatorChar));

        return string.Equals(newPath, path, StringComparison.Ordinal)
            ? Make(newPath, RenameStatus.Unchanged)
            : Make(newPath, RenameStatus.Rename);
    }

    private void TidyUp(string from, HashSet<string> destinations, IReadOnlyList<MediaFolder> folders)
    {
        try
        {
            if (!Directory.Exists(from) || Directory.EnumerateFiles(from).Any(AudioFormats.IsSupported))
            {
                return;
            }

            var root = LibraryScanner.FolderFor(Path.Combine(from, "_"), folders) is { } folder
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Location))
                : null;

            // Every track went to one new folder: take the rest along.
            if (destinations.Count == 1 && destinations.Single() is var to && !string.Equals(to, from, StringComparison.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(from))
                {
                    var target = Path.Combine(to, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        File.Move(file, target);
                    }
                }
            }

            for (var directory = from;
                 root is not null && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(directory).Any();
                 directory = Path.GetDirectoryName(directory)!)
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't tidy up {Directory} after renaming", from);
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Lets only one backup run at a time (scheduled and manual alike), and
/// wakes <see cref="BackupWorker"/> when the schedule changes.
/// </summary>
public class BackupCoordinator
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly SemaphoreSlim _scheduleChanged = new(0, 1);

    public bool IsRunning => _running.CurrentCount == 0;

    public bool TryStart() => _running.Wait(0);

    public void Finished() => _running.Release();

    public void ScheduleChanged()
    {
        try
        {
            _scheduleChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; one wake-up covers it.
        }
    }

    public Task<bool> WaitForScheduleChangeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _scheduleChanged.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Writes backup zips of the database plus the key material needed to use
/// it (jwt.key, the Data Protection keys that decrypt stored secrets) into
/// Backup:Directory, prunes old ones, and keeps the schedule and last result
/// in AppSettings. Only files named like its own backups are ever listed,
/// served or deleted.
/// </summary>
public partial class BackupService
{
    public const string ManualTrigger = "manual";
    public const string ScheduledTrigger = "scheduled";

    private const string ScheduleSetting = "Backup:Schedule";
    private const string LastRunSetting = "Backup:LastRun";
    private const string FilePrefix = "fossegrim-backup-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FossegrimDbContext _db;
    private readonly BackupCoordinator _coordinator;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        FossegrimDbContext db,
        BackupCoordinator coordinator,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<BackupService> logger)
    {
        _db = db;
        _coordinator = coordinator;
        _configuration = configuration;
        _time = time;
        _logger = logger;
        // Docker sets Backup:Directory to the /backups volume; locally it's
        // a backups folder next to the Api project.
        Directory = configuration["Backup:Directory"] is { Length: > 0 } directory
            ? Path.GetFullPath(directory)
            : Path.Combine(environment.ContentRootPath, "backups");
    }

    public string Directory { get; }

    public async Task<BackupStatusDto> GetStatusAsync()
    {
        var (schedule, _) = await GetScheduleAsync();
        return new BackupStatusDto(
            Directory,
            schedule,
            await GetNextRunAsync(),
            await GetLastRunAsync(),
            _coordinator.IsRunning,
            ListBackups());
    }

    public async Task<(BackupScheduleDto Schedule, DateTime SavedAt)> GetScheduleAsync()
    {
        var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ScheduleSetting);
        var schedule = setting?.Value is { } value ? JsonSerializer.Deserialize<BackupScheduleDto>(value, Json) : null;
        return (schedule ?? BackupScheduleDto.Default, setting?.UpdatedAt ?? DateTime.MinValue);
    }

    /// <summary>Null when valid, otherwise what's wrong with it.</summary>
    public static string? Validate(BackupScheduleDto schedule) => schedule switch
    {
        { IntervalHours: < 1 or > BackupSchedules.MaxIntervalHours } => $"Interval must be between 1 and {BackupSchedules.MaxIntervalHours} hours.",
        { DayOfMonth: < 1 or > 31 } => "Day of month must be between 1 and 31.",
        { Frequency: BackupFrequency.Weekly, DaysOfWeek.Count: 0 } => "Pick at least one day of the week.",
        { KeepLast: < 1 } => "Keep at least 1 backup, or leave it blank for no limit.",
        { MaxAgeDays: < 1 } => "Maximum age must be at least 1 day, or blank for no limit.",
        _ when BackupSchedules.FindTimeZone(schedule.TimeZone) is null => $"Unknown time zone '{schedule.TimeZone}'.",
        _ => null
    };

    public async Task SaveScheduleAsync(BackupScheduleDto schedule)
    {
        schedule = schedule with { DaysOfWeek = schedule.DaysOfWeek.Distinct().Order().ToList() };
        await UpsertAsync(ScheduleSetting, JsonSerializer.Serialize(schedule, Json));
        _coordinator.ScheduleChanged();
    }

    public async Task<BackupRunDto?> GetLastRunAsync()
    {
        var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == LastRunSetting);
        return setting?.Value is { } value ? JsonSerializer.Deserialize<BackupRunDto>(value, Json) : null;
    }

    /// <summary>
    /// When the next scheduled backup is due, counted from the later of the
    /// last attempt and the last schedule change. A time in the past means a
    /// run was missed (e.g. the server was down) and should happen now.
    /// </summary>
    public async Task<DateTime?> GetNextRunAsync()
    {
        var (schedule, savedAt) = await GetScheduleAsync();
        var lastRun = (await GetLastRunAsync())?.StartedAt ?? DateTime.MinValue;
        var from = lastRun > savedAt ? lastRun : savedAt;
        // SQLite hands DateTimes back unspecified; they were stored as UTC.
        return BackupSchedules.NextRun(schedule, from == DateTime.MinValue ? _time.GetUtcNow().UtcDateTime : DateTime.SpecifyKind(from, DateTimeKind.Utc));
    }

    /// <summary>Runs a backup now; null if one is already running.</summary>
    public async Task<BackupRunDto?> RunAsync(string trigger, CancellationToken cancellationToken = default)
    {
        if (!_coordinator.TryStart())
        {
            return null;
        }

        var startedAt = _time.GetUtcNow().UtcDateTime;
        BackupRunDto result;
        try
        {
            var path = await CreateBackupAsync(startedAt, trigger, cancellationToken);
            result = new BackupRunDto(startedAt, trigger, true, Path.GetFileName(path), new FileInfo(path).Length, null);
            _logger.LogInformation("Backup written to {Path}", path);

            var (schedule, _) = await GetScheduleAsync();
            Prune(schedule.KeepLast, schedule.MaxAgeDays, startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Backup to {Directory} failed", Directory);
            result = new BackupRunDto(startedAt, trigger, false, null, null, ex.Message);
        }
        finally
        {
            _coordinator.Finished();
        }

        await UpsertAsync(LastRunSetting, JsonSerializer.Serialize(result, Json));
        return result;
    }

    public IReadOnlyList<BackupFileDto> ListBackups()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return new DirectoryInfo(Directory)
            .EnumerateFiles($"{FilePrefix}*.zip")
            .Select(file => TryParseCreatedAt(file.Name, out var createdAt) ? new BackupFileDto(file.Name, createdAt, file.Length) : null)
            .OfType<BackupFileDto>()
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The backup's full path, or null if the name isn't one of ours or it doesn't exist.</summary>
    public string? FindBackup(string fileName)
    {
        if (!TryParseCreatedAt(fileName, out _))
        {
            return null;
        }

        var path = Path.Combine(Directory, fileName);
        return File.Exists(path) ? path : null;
    }

    public bool DeleteBackup(string fileName)
    {
        var path = FindBackup(fileName);
        if (path is null)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private async Task<string> CreateBackupAsync(DateTime startedAt, string trigger, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var stamp = startedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(Directory, $"{FilePrefix}{stamp}.zip");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(Directory, $"{FilePrefix}{stamp}-{n}.zip");
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), $"fossegrim-backup-{Guid.NewGuid():N}");
        var partialPath = path + ".partial";
        System.IO.Directory.CreateDirectory(workDirectory);
        try
        {
            // SQLite's online backup API gives a consistent copy while the app
            // keeps reading and writing; copying the file directly could catch
            // it mid-write.
            var databaseCopy = Path.Combine(workDirectory, "fossegrim.db");
            await using (var source = new SqliteConnection(_db.Database.GetConnectionString()))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseCopy, Pooling = false }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            var files = new List<string> { "fossegrim.db" };
            await using (var stream = new FileStream(partialPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(databaseCopy, "fossegrim.db", CompressionLevel.Optimal);

                if (_configuration["Jwt:KeyFile"] is { Length: > 0 } keyFile && File.Exists(keyFile))
                {
                    zip.CreateEntryFromFile(keyFile, "jwt.key");
                    files.Add("jwt.key");
                }

                if (_configuration["DataProtection:KeysDirectory"] is { Length: > 0 } keysDirectory && System.IO.Directory.Exists(keysDirectory))
                {
                    foreach (var keyPath in System.IO.Directory.EnumerateFiles(keysDirectory))
                    {
                        var entryName = $"dataprotection-keys/{Path.GetFileName(keyPath)}";
                        zip.CreateEntryFromFile(keyPath, entryName);
                        files.Add(entryName);
                    }
                }

                var manifest = new
                {
                    format = 1,
                    app = "Fossegrim",
                    version = _configuration["APP_VERSION"] is { Length: > 0 } version ? version : "dev",
                    createdAt = startedAt,
                    trigger,
                    lastMigration = (await _db.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault(),
                    files
                };
                await using var manifestStream = zip.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            }

            // Only complete zips get a name the listing and pruning recognise.
            File.Move(partialPath, path);
            return path;
        }
        finally
        {
            File.Delete(partialPath);
            System.IO.Directory.Delete(workDirectory, recursive: true);
        }
    }

    // Applies retention to every backup in the folder, manual ones included.
    // The newest is always kept, whatever the limits say.
    private void Prune(int? keepLast, int? maxAgeDays, DateTime now)
    {
        var backups = ListBackups();
        var doomed = backups
            .Skip(1)
            .Where((backup, index) =>
                (keepLast is { } keep && index + 1 >= keep) ||
                (maxAgeDays is { } days && backup.CreatedAt < now.AddDays(-days)));

        foreach (var backup in doomed)
        {
            try
            {
                File.Delete(Path.Combine(Directory, backup.FileName));
                _logger.LogInformation("Pruned old backup {FileName}", backup.FileName);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Couldn't prune old backup {FileName}", backup.FileName);
            }
        }
    }

    private static bool TryParseCreatedAt(string fileName, out DateTime createdAt)
    {
        var match = BackupFileName().Match(fileName);
        createdAt = default;
        return match.Success && DateTime.TryParseExact(
            match.Groups[1].Value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out createdAt);
    }

    private async Task UpsertAsync(string key, string value)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = _time.GetUtcNow().UtcDateTime });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        }
        await _db.SaveChangesAsync();
    }

    [GeneratedRegex(@"^fossegrim-backup-(\d{8}-\d{6})(?:-\d+)?\.zip$")]
    private static partial Regex BackupFileName();
}

/// <summary>
/// Runs scheduled backups: sleeps until the next one is due (re-checking at
/// least hourly, and straight away when the schedule changes), and catches
/// up once at startup if a run was missed while the server was down. Turned
/// off with Backup:WorkerEnabled = false (tests).
/// </summary>
public class BackupWorker : BackgroundService
{
    private static readonly TimeSpan MaxIdle = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackupCoordinator _coordinator;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupWorker> _logger;

    public BackupWorker(IServiceScopeFactory scopeFactory, BackupCoordinator coordinator, TimeProvider time, ILogger<BackupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = MaxIdle;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var backups = scope.ServiceProvider.GetRequiredService<BackupService>();

                if (await backups.GetNextRunAsync() is { } nextRun)
                {
                    var untilDue = nextRun - _time.GetUtcNow().UtcDateTime;
                    if (untilDue <= TimeSpan.Zero)
                    {
                        // Null means a manual backup is mid-run; check again shortly.
                        await backups.RunAsync(BackupService.ScheduledTrigger, stoppingToken);
                        wait = MinWait;
                    }
                    else
                    {
                        wait = untilDue < MaxIdle ? untilDue : MaxIdle;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup scheduling pass failed; trying again in {Wait}", MaxIdle);
            }

            try
            {
                await _coordinator.WaitForScheduleChangeAsync(wait < MinWait ? MinWait : wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

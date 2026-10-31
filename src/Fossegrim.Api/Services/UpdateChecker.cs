using System.Text.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// The version this server is running: APP_VERSION, set from the release's
/// git tag via the image's VERSION build-arg (see Dockerfile), or "dev" for
/// anything built without it. Release is null when it isn't a release version.
/// </summary>
public record AppVersion(string Value)
{
    public ReleaseVersion? Release { get; } = ReleaseVersion.TryParse(Value, out var release) ? release : null;
}

/// <summary>
/// The outcome of the last update check. Kept in memory: the worker checks
/// again shortly after every start, so there's nothing worth persisting.
/// </summary>
public class UpdateCheckState
{
    private Snapshot _current = new(null, null, null);

    public Snapshot Current => Volatile.Read(ref _current);

    public void Succeeded(DateTime checkedAt, ReleaseVersion? latest) =>
        Volatile.Write(ref _current, new Snapshot(latest, checkedAt, latest is null ? "No releases found on Docker Hub." : null));

    // A failed check keeps the last version found, so a Docker Hub outage
    // doesn't hide an update that was already known about.
    public void Failed(DateTime checkedAt, string error) =>
        Volatile.Write(ref _current, Current with { CheckedAt = checkedAt, Error = error });

    public record Snapshot(ReleaseVersion? Latest, DateTime? CheckedAt, string? Error);
}

/// <summary>
/// Compares the running version with the newest stable release tag on
/// Docker Hub. Automatic (daily) checks can be turned off by an admin; the
/// setting lives in AppSettings and defaults to on.
/// </summary>
public class UpdateChecker
{
    private const string EnabledSetting = "UpdateCheck:Enabled";

    private readonly FossegrimDbContext _db;
    private readonly DockerHubClient _dockerHub;
    private readonly UpdateCheckState _state;
    private readonly AppVersion _version;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly string _repository;

    public UpdateChecker(
        FossegrimDbContext db,
        DockerHubClient dockerHub,
        UpdateCheckState state,
        AppVersion version,
        TimeProvider time,
        IConfiguration configuration,
        ILogger<UpdateChecker> logger)
    {
        _db = db;
        _dockerHub = dockerHub;
        _state = state;
        _version = version;
        _time = time;
        _logger = logger;
        _repository = configuration["UpdateCheck:Repository"] is { Length: > 0 } repository ? repository : DockerHubClient.DefaultRepository;
    }

    public bool CanCheck => _version.Release is not null;

    public async Task<bool> IsEnabledAsync()
    {
        var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EnabledSetting);
        return !bool.TryParse(setting?.Value, out var enabled) || enabled;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        var setting = await _db.AppSettings.FindAsync(EnabledSetting);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = EnabledSetting, Value = enabled.ToString() });
        }
        else
        {
            setting.Value = enabled.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>Asks Docker Hub for the latest release; does nothing for development builds.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCheck)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var tags = await _dockerHub.GetTagNamesAsync(_repository, cancellationToken);
            var latest = tags
                .Select(tag => ReleaseVersion.TryParse(tag, out var version) ? version : null)
                .Where(version => version is { IsPrerelease: false })
                .Max();
            _state.Succeeded(now, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Update check against Docker Hub ({Repository}) failed", _repository);
            _state.Failed(now, ex is TaskCanceledException ? "Docker Hub didn't respond in time." : $"Couldn't reach Docker Hub: {ex.Message}");
        }
    }

    public async Task<UpdateStatusDto> GetStatusAsync()
    {
        var snapshot = _state.Current;
        var latest = CanCheck ? snapshot.Latest : null;
        return new UpdateStatusDto(
            _version.Value,
            CanCheck,
            await IsEnabledAsync(),
            latest?.ToString(),
            latest is not null && latest > _version.Release!,
            CanCheck ? snapshot.CheckedAt : null,
            CanCheck ? snapshot.Error : null,
            latest is null ? null : $"https://hub.docker.com/r/{_repository}/tags?name={latest}");
    }
}

/// <summary>
/// Checks for updates a minute after startup and then once a day, while the
/// admin setting allows it. Only registered for release builds, and turned
/// off entirely with UpdateCheck:WorkerEnabled = false (tests).
/// </summary>
public class UpdateCheckWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdateCheckWorker> _logger;

    public UpdateCheckWorker(IServiceScopeFactory scopeFactory, ILogger<UpdateCheckWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var checker = scope.ServiceProvider.GetRequiredService<UpdateChecker>();
                    if (await checker.IsEnabledAsync())
                    {
                        await checker.CheckAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Update check failed; trying again in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

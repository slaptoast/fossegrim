namespace Fossegrim.Api.Services;

/// <summary>
/// Wakes <see cref="ScrobbleWorker"/> as soon as a new play is marked
/// Pending, instead of waiting for its next scheduled check.
/// </summary>
public class ScrobbleSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; one wake-up covers it.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Background loop that sends Pending plays to Last.fm: right after a play is
/// recorded (via <see cref="ScrobbleSignal"/>), when a retry falls due, and
/// at least every few minutes. Turned off with Scrobbling:Enabled = false.
/// </summary>
public class ScrobbleWorker : BackgroundService
{
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScrobbleSignal _signal;
    private readonly ILogger<ScrobbleWorker> _logger;

    public ScrobbleWorker(IServiceScopeFactory scopeFactory, ScrobbleSignal signal, ILogger<ScrobbleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
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
                var scrobbler = scope.ServiceProvider.GetRequiredService<Scrobbler>();

                while (await scrobbler.ProcessDueAsync(DateTime.UtcNow, stoppingToken) == Scrobbler.BatchSize)
                {
                }

                if (await scrobbler.NextDueAsync(stoppingToken) is { } nextDue)
                {
                    wait = TimeSpan.FromTicks(Math.Clamp((nextDue - DateTime.UtcNow).Ticks, MinWait.Ticks, MaxIdle.Ticks));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrobbling pass failed; trying again in {Wait}", MaxIdle);
            }

            try
            {
                await _signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

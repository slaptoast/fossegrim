using System.Threading.Channels;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services.Library;

namespace Fossegrim.Api.Services;

/// <summary>
/// Held while anything rearranges the library's files: a scan (by
/// <see cref="ScanWorker"/>) or a rename, so the two never run at once.
/// </summary>
public class LibraryLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public bool TryEnter() => _semaphore.Wait(0);

    public void Release() => _semaphore.Release();
}

/// <summary>What to scan, and who asked; see <see cref="ScanQueue.Enqueue"/>.</summary>
public record ScanJobSpec(IReadOnlyList<ScanTarget> Targets, bool Full, string Description, string Source);

/// <summary>
/// Scans waiting to run, the one running and the last few finished, all in
/// memory. Scans run one at a time, in order, on <see cref="ScanWorker"/>.
/// </summary>
public class ScanQueue
{
    private const int RecentLimit = 10;

    private readonly Channel<ScanJob> _channel = Channel.CreateUnbounded<ScanJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _lock = new();
    private readonly List<ScanJob> _queued = [];
    private readonly LinkedList<ScanJob> _recent = new();
    private readonly TimeProvider _time;
    private ScanJob? _current;

    public ScanQueue(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Queues a scan, unless the same scan is already waiting, in which case
    /// that one is returned: asking twice doesn't scan twice.
    /// </summary>
    public ScanJobDto Enqueue(ScanJobSpec spec)
    {
        lock (_lock)
        {
            var duplicate = _queued.FirstOrDefault(job => job.Spec.Full == spec.Full && job.Spec.Targets.SequenceEqual(spec.Targets));
            if (duplicate is not null)
            {
                return duplicate.ToDto();
            }

            var job = new ScanJob(Guid.NewGuid(), spec, _time.GetUtcNow().UtcDateTime);
            _queued.Add(job);
            _channel.Writer.TryWrite(job);
            return job.ToDto();
        }
    }

    public ScanStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new ScanStatusDto(_current?.ToDto(), _queued.Select(j => j.ToDto()).ToList(), _recent.Select(j => j.ToDto()).ToList());
        }
    }

    public ScanJobDto? Find(Guid id)
    {
        lock (_lock)
        {
            return (_current?.Id == id ? _current : null)?.ToDto()
                ?? _queued.FirstOrDefault(j => j.Id == id)?.ToDto()
                ?? _recent.FirstOrDefault(j => j.Id == id)?.ToDto();
        }
    }

    internal async ValueTask<ScanJob> DequeueAsync(CancellationToken cancellationToken) =>
        await _channel.Reader.ReadAsync(cancellationToken);

    internal void Started(ScanJob job)
    {
        lock (_lock)
        {
            _queued.Remove(job);
            _current = job;
            job.Start(_time.GetUtcNow().UtcDateTime);
        }
    }

    internal void Finished(ScanJob job, ScanSummary? summary, string? error)
    {
        lock (_lock)
        {
            job.Finish(_time.GetUtcNow().UtcDateTime, summary, error);
            _current = null;
            _recent.AddFirst(job);
            while (_recent.Count > RecentLimit)
            {
                _recent.RemoveLast();
            }
        }
    }
}

/// <summary>One scan's state. Progress is reported from the scanner's worker threads.</summary>
public class ScanJob : IProgress<ScanProgress>
{
    private readonly object _lock = new();
    private ScanJobState _state = ScanJobState.Queued;
    private ScanPhase _phase = ScanPhase.Waiting;
    private int _processed;
    private int _total;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private ScanSummary? _summary;
    private string? _error;

    public ScanJob(Guid id, ScanJobSpec spec, DateTime queuedAt)
    {
        Id = id;
        Spec = spec;
        QueuedAt = queuedAt;
    }

    public Guid Id { get; }

    public ScanJobSpec Spec { get; }

    public DateTime QueuedAt { get; }

    public void Report(ScanProgress value)
    {
        lock (_lock)
        {
            // Reports from parallel readers can arrive out of order.
            if (value.Phase == _phase && value.Processed < _processed)
            {
                return;
            }
            (_phase, _processed, _total) = (value.Phase, value.Processed, value.Total);
        }
    }

    internal void Start(DateTime at)
    {
        lock (_lock)
        {
            (_state, _startedAt, _phase) = (ScanJobState.Running, at, ScanPhase.Counting);
        }
    }

    internal void Finish(DateTime at, ScanSummary? summary, string? error)
    {
        lock (_lock)
        {
            (_state, _finishedAt, _summary, _error, _phase) =
                (error is null ? ScanJobState.Completed : ScanJobState.Failed, at, summary, error, ScanPhase.Done);
        }
    }

    public ScanJobDto ToDto()
    {
        lock (_lock)
        {
            return new ScanJobDto(Id, Spec.Description, Spec.Full, Spec.Source, _state, _phase, _processed, _total,
                QueuedAt, _startedAt, _finishedAt, _summary, _error);
        }
    }
}

/// <summary>Runs queued scans one after another, holding <see cref="LibraryLock"/> while each runs.</summary>
public class ScanWorker : BackgroundService
{
    private readonly ScanQueue _queue;
    private readonly LibraryLock _libraryLock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScanWorker> _logger;

    public ScanWorker(ScanQueue queue, LibraryLock libraryLock, IServiceScopeFactory scopeFactory, ILogger<ScanWorker> logger)
    {
        _queue = queue;
        _libraryLock = libraryLock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ScanJob job;
            try
            {
                job = await _queue.DequeueAsync(stoppingToken);
                await _libraryLock.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _queue.Started(job);
                _logger.LogInformation("Scan started: {Description} ({Kind})", job.Spec.Description, job.Spec.Full ? "full" : "quick");
                using var scope = _scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
                var summary = await scanner.ScanAsync(job.Spec.Targets, job.Spec.Full, job, stoppingToken);
                _queue.Finished(job, summary, null);
                _logger.LogInformation("Scan finished: {Description}: {Summary}", job.Spec.Description, summary);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _queue.Finished(job, null, "The server shut down during the scan.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan failed: {Description}", job.Spec.Description);
                _queue.Finished(job, null, ex.Message);
            }
            finally
            {
                _libraryLock.Release();
            }
        }
    }
}

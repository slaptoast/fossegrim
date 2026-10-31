using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// The server's scan queue as admins see it, shared by the progress bars and
/// the top-bar indicator. Polls once a second while a scan is queued or
/// running and stops when the queue is empty; starting a scan (or calling
/// <see cref="RefreshAsync"/>) wakes it up again.
/// </summary>
public class ScanStatusService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly FossegrimApiClient _apiClient;
    private CancellationTokenSource? _polling;

    public ScanStatusService(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public ScanStatusDto? Status { get; private set; }

    public bool IsBusy => Status is { Current: not null } or { Queued.Count: > 0 };

    public event Action? Changed;

    // Raised once per scan that finishes while this page is watching, so
    // lists can reload what the scan changed.
    public event Action<ScanJobDto>? ScanFinished;

    public async Task<ScanJobDto> StartAsync(StartScanRequest request) => Watch(await _apiClient.StartScanAsync(request));

    public async Task<ScanJobDto> RescanAlbumAsync(Guid albumId) => Watch(await _apiClient.RescanAlbumAsync(albumId));

    public async Task<ScanJobDto> RescanArtistAsync(Guid artistId) => Watch(await _apiClient.RescanArtistAsync(artistId));

    public async Task RefreshAsync()
    {
        await PollOnceAsync();
        EnsurePolling();
    }

    private ScanJobDto Watch(ScanJobDto job)
    {
        _ = RefreshAsync();
        return job;
    }

    private void EnsurePolling()
    {
        if (!IsBusy || _polling is not null)
        {
            return;
        }

        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken) && await PollOnceAsync() && IsBusy)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _polling = null;
        }
    }

    // False if the status couldn't be read (e.g. signed out), which stops polling.
    private async Task<bool> PollOnceAsync()
    {
        ScanStatusDto status;
        try
        {
            status = await _apiClient.GetScanStatusAsync();
        }
        catch
        {
            return false;
        }

        var watched = Status is null ? [] : Status.Queued.Select(j => j.Id).Append(Status.Current?.Id ?? Guid.Empty).ToHashSet();
        Status = status;
        Changed?.Invoke();

        foreach (var finished in status.Recent.Where(j => watched.Contains(j.Id)))
        {
            ScanFinished?.Invoke(finished);
        }
        return true;
    }

    public void Dispose() => _polling?.Cancel();
}

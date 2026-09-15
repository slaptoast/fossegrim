using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// Holds the currently-playing track so a persistent player bar in MainLayout
/// keeps playing across navigation, instead of a dedicated per-track page.
/// </summary>
public class PlayerState
{
    private readonly FossegrimApiClient _apiClient;
    private readonly List<MediaItemDto> _queue = [];
    private int _queueIndex = -1;

    public PlayerState(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public MediaItemDto? CurrentTrack { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event Action? Changed;

    public Task PlayAsync(MediaItemDto track) => PlayQueueAsync([track]);

    public Task PlayQueueAsync(IReadOnlyList<MediaItemDto> tracks)
    {
        _queue.Clear();
        _queue.AddRange(tracks);
        return PlayAtAsync(0);
    }

    public Task PlayNextAsync() => _queueIndex + 1 < _queue.Count
        ? PlayAtAsync(_queueIndex + 1)
        : Task.CompletedTask;

    private async Task PlayAtAsync(int index)
    {
        _queueIndex = index;
        var track = _queue[index];

        CurrentTrack = track;
        ErrorMessage = null;
        StreamUrl = null;
        Changed?.Invoke();

        try
        {
            StreamUrl = await _apiClient.GetStreamUrlAsync(track.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not play track: {ex.Message}";
        }

        Changed?.Invoke();
    }

    public void Stop()
    {
        _queue.Clear();
        _queueIndex = -1;
        CurrentTrack = null;
        StreamUrl = null;
        ErrorMessage = null;
        Changed?.Invoke();
    }
}

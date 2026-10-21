using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// Holds the currently-playing track so a persistent player bar in MainLayout
/// keeps playing across navigation, instead of a dedicated per-track page.
/// Backed by the user's server-side Queue playlist, so what's queued and
/// which track is "now playing" survives a reload or a switch to another client.
/// </summary>
public class PlayerState
{
    private readonly FossegrimApiClient _apiClient;
    private readonly List<MediaItemDto> _queue = [];
    private int _queueIndex = -1;

    public PlayerState(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
        _ = HydrateFromServerAsync();
    }

    public MediaItemDto? CurrentTrack { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    // False only for the track restored from the server on startup, so
    // reloading the page doesn't unexpectedly start audio playing.
    public bool AutoPlay { get; private set; }

    public event Action? Changed;

    public Task PlayAsync(MediaItemDto track) => PlayQueueAsync([track]);

    public Task PlayQueueAsync(IReadOnlyList<MediaItemDto> tracks)
    {
        _queue.Clear();
        _queue.AddRange(tracks);
        return PlayAtAsync(0, persistWholeQueue: true);
    }

    public Task PlayNextAsync() => _queueIndex + 1 < _queue.Count
        ? PlayAtAsync(_queueIndex + 1, persistWholeQueue: false)
        : Task.CompletedTask;

    // Inserts the given tracks immediately after the currently-playing item
    // (or at the very start, if nothing is currently playing).
    public async Task AddToQueueNextAsync(IEnumerable<Guid> mediaItemIds)
    {
        var ids = mediaItemIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var queue = await _apiClient.GetQueueAsync();
            var afterAdd = await _apiClient.AddPlaylistItemsAsync(queue.Id, ids);

            var ordered = afterAdd.Items.OrderBy(i => i.Position).ToList();
            var newItems = ordered.TakeLast(ids.Count).ToList();
            var afterId = ordered.FirstOrDefault(i => i.MediaItem.Id == queue.CurrentMediaItemId)?.Id;

            var latest = afterAdd;
            foreach (var newItem in newItems)
            {
                latest = await _apiClient.MovePlaylistItemAsync(queue.Id, newItem.Id, afterId);
                afterId = newItem.Id;
            }

            SyncQueueList(latest, out _, out _);
            await EnsureCurrentTrackAsync();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not add to queue: {ex.Message}";
            Changed?.Invoke();
        }
    }

    public async Task AddToQueueEndAsync(IEnumerable<Guid> mediaItemIds)
    {
        var ids = mediaItemIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var queue = await _apiClient.GetQueueAsync();
            var updated = await _apiClient.AddPlaylistItemsAsync(queue.Id, ids);
            SyncQueueList(updated, out _, out _);
            await EnsureCurrentTrackAsync();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not add to queue: {ex.Message}";
            Changed?.Invoke();
        }
    }

    private async Task PlayAtAsync(int index, bool persistWholeQueue)
    {
        _queueIndex = index;
        var track = _queue[index];

        CurrentTrack = track;
        AutoPlay = true;
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

        try
        {
            if (persistWholeQueue)
            {
                await _apiClient.SetQueueAsync(_queue.Select(t => t.Id), track.Id);
            }
            else
            {
                await _apiClient.SetQueueCurrentTrackAsync(track.Id);
            }
        }
        catch
        {
            // Best-effort: losing sync with the server shouldn't block local playback.
        }
    }

    public void Stop()
    {
        _queue.Clear();
        _queueIndex = -1;
        CurrentTrack = null;
        StreamUrl = null;
        ErrorMessage = null;
        AutoPlay = false;
        Changed?.Invoke();
    }

    // Replaces the local queue snapshot with the server's authoritative order/position,
    // so PlayNextAsync keeps advancing correctly after out-of-band edits (e.g. right-click add).
    private void SyncQueueList(PlaylistDetailDto queue, out List<MediaItemDto> tracks, out int currentIndex)
    {
        var items = queue.Items.OrderBy(i => i.Position).ToList();
        tracks = items.Select(i => PlaylistTrackMapper.ToMediaItemDto(i.MediaItem)).ToList();
        currentIndex = queue.CurrentMediaItemId is null
            ? -1
            : items.FindIndex(i => i.MediaItem.Id == queue.CurrentMediaItemId);

        _queue.Clear();
        _queue.AddRange(tracks);
        _queueIndex = currentIndex;
    }

    // When nothing is "now playing" (e.g. the first right-click add into an empty queue),
    // surface the first queued track in the player bar, paused, so the queue is visible and
    // playable instead of filling up out of sight. PlayNextAsync then advances through it.
    private async Task EnsureCurrentTrackAsync()
    {
        if (_queueIndex >= 0 || _queue.Count == 0)
        {
            return;
        }

        var existingIndex = CurrentTrack is null ? -1 : _queue.FindIndex(t => t.Id == CurrentTrack.Id);
        if (existingIndex >= 0)
        {
            _queueIndex = existingIndex;
        }
        else
        {
            _queueIndex = 0;
            CurrentTrack = _queue[0];
            AutoPlay = false;
            ErrorMessage = null;
            StreamUrl = null;

            try
            {
                StreamUrl = await _apiClient.GetStreamUrlAsync(CurrentTrack.Id);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not load track: {ex.Message}";
            }
        }

        try
        {
            await _apiClient.SetQueueCurrentTrackAsync(CurrentTrack!.Id);
        }
        catch
        {
            // Best-effort: losing sync with the server shouldn't block local playback.
        }
    }

    private async Task HydrateFromServerAsync()
    {
        try
        {
            var queue = await _apiClient.GetQueueAsync();
            SyncQueueList(queue, out var tracks, out var currentIndex);
            if (tracks.Count == 0)
            {
                return;
            }

            if (currentIndex < 0)
            {
                currentIndex = 0;
                _queueIndex = 0;

                // Persist the default so a later "Add to next in queue" lands after this
                // track rather than in front of it.
                try
                {
                    await _apiClient.SetQueueCurrentTrackAsync(tracks[0].Id);
                }
                catch
                {
                    // Best-effort, as in PlayAtAsync.
                }
            }

            CurrentTrack = tracks[currentIndex];
            AutoPlay = false;

            try
            {
                StreamUrl = await _apiClient.GetStreamUrlAsync(CurrentTrack.Id);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not restore playback: {ex.Message}";
            }

            Changed?.Invoke();
        }
        catch
        {
            // No persisted queue yet, or the user isn't authenticated - nothing to restore.
        }
    }
}

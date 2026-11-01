using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

// One entry in the queue. ItemId is the server's queue entry id, so a track
// queued twice is still two distinct entries. While a newly started queue is
// being saved, entries get a temporary local id (unique, so the Up next list
// can still key on it) and IsSaved is false.
public record QueueEntry(Guid ItemId, MediaItemDto Track, bool IsSaved = true);

/// <summary>
/// Holds the queue and the currently-playing entry so a persistent player bar
/// in MainLayout keeps playing across navigation. Backed by the user's
/// server-side Queue playlist, so what's queued and what's "now playing"
/// survive a reload or a switch to another client.
///
/// Every change to the queue runs one at a time (see <see cref="_lock"/>), so
/// e.g. "play this album" followed straight away by "add to queue" can't
/// interleave and lose one of them.
/// </summary>
public class PlayerState
{
    private readonly FossegrimApiClient _apiClient;
    private readonly ToastService _toasts;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<QueueEntry> _queue = [];
    private int _index = -1;
    private Guid? _queueId;

    public PlayerState(FossegrimApiClient apiClient, ToastService toasts)
    {
        _apiClient = apiClient;
        _toasts = toasts;
        Initialized = HydrateFromServerAsync();
    }

    // Completes once the queue saved on the server has been restored.
    public Task Initialized { get; }

    public IReadOnlyList<QueueEntry> Queue => _queue;
    public int CurrentIndex => _index;
    public QueueEntry? CurrentEntry => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
    public bool HasNext => _index + 1 < _queue.Count;
    public bool HasPrevious => _index > 0;

    public MediaItemDto? CurrentTrack { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    // False for a track that's only been loaded (restored after a reload, or
    // the first track added to an empty queue), so audio doesn't start by surprise.
    public bool AutoPlay { get; private set; }

    public event Action? Changed;

    public Task PlayAsync(MediaItemDto track) => PlayQueueAsync([track]);

    // Replaces the queue with these tracks and starts playing the first.
    public Task PlayQueueAsync(IReadOnlyList<MediaItemDto> tracks) => WithLockAsync(async () =>
    {
        if (tracks.Count == 0)
        {
            return;
        }

        // Start the audio straight away; the queue entries get their ids once saved.
        _queue = tracks.Select(t => new QueueEntry(Guid.NewGuid(), t, IsSaved: false)).ToList();
        _index = 0;
        await LoadCurrentAsync(autoPlay: true);

        try
        {
            Adopt(await _apiClient.SetQueueAsync(tracks.Select(t => t.Id), currentIndex: 0));
            Changed?.Invoke();
        }
        catch
        {
            // Best-effort: losing sync with the server shouldn't block local playback.
        }
    });

    public Task PlayNextAsync() => WithLockAsync(() => HasNext ? MoveToAsync(_index + 1) : Task.CompletedTask);

    public Task PlayPreviousAsync() => WithLockAsync(() => HasPrevious ? MoveToAsync(_index - 1) : Task.CompletedTask);

    public Task JumpToAsync(Guid itemId) => WithLockAsync(() =>
    {
        var index = _queue.FindIndex(e => e.ItemId == itemId);
        return index >= 0 ? MoveToAsync(index) : Task.CompletedTask;
    });

    // Removes an entry other than the one playing (the Up next panel doesn't offer that).
    public Task RemoveAsync(Guid itemId) => WithLockAsync(async () =>
    {
        if (_queueId is not { } queueId || CurrentEntry?.ItemId == itemId ||
            _queue.Find(e => e.ItemId == itemId) is not { IsSaved: true })
        {
            return;
        }

        try
        {
            await _apiClient.RemovePlaylistItemAsync(queueId, itemId);
            Adopt(await _apiClient.GetQueueAsync());
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't remove from queue: {ex.Message}");
        }

        Changed?.Invoke();
    });

    // Inserts the tracks right after the playing entry (or at the start when
    // nothing is playing).
    public Task AddToQueueNextAsync(IEnumerable<Guid> mediaItemIds) => AddToQueueAsync(mediaItemIds.ToList(), playNext: true);

    public Task AddToQueueEndAsync(IEnumerable<Guid> mediaItemIds) => AddToQueueAsync(mediaItemIds.ToList(), playNext: false);

    private Task AddToQueueAsync(List<Guid> ids, bool playNext) => WithLockAsync(async () =>
    {
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var queue = await _apiClient.GetQueueAsync();
            var latest = await _apiClient.AddPlaylistItemsAsync(queue.Id, ids);

            if (playNext)
            {
                // New entries are appended; move each one after the playing entry (and its predecessor).
                var newItems = latest.Items.OrderBy(i => i.Position).TakeLast(ids.Count).ToList();
                var afterId = queue.CurrentItemId;
                foreach (var newItem in newItems)
                {
                    latest = await _apiClient.MovePlaylistItemAsync(queue.Id, newItem.Id, afterId);
                    afterId = newItem.Id;
                }
            }

            Adopt(latest);
            await LoadFirstIfNothingPlayingAsync();
            _toasts.Show(AddedMessage(ids, playNext));
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't add to queue: {ex.Message}");
        }

        Changed?.Invoke();
    });

    // Called when the <audio> element actually starts or resumes playing (not
    // when a track is merely loaded, e.g. restored without autoplay). The
    // server ignores this unless the user has linked Last.fm.
    public async Task ReportNowPlayingAsync()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        try
        {
            await _apiClient.UpdateLastFmNowPlayingAsync(CurrentTrack.Id);
        }
        catch
        {
            // Best-effort: a Last.fm hiccup shouldn't surface in the player.
        }
    }

    // Records a play in the user's listening history once the player has
    // heard enough of the current track (see wwwroot/js/listenTracker.js).
    public async Task RecordListenAsync(DateTimeOffset startedAt, TimeSpan listenedFor)
    {
        if (CurrentTrack is null)
        {
            return;
        }

        try
        {
            await _apiClient.RecordPlayAsync(CurrentTrack.Id, startedAt, listenedFor);
        }
        catch
        {
            // Best-effort, like now-playing: a lost listen shouldn't disrupt playback.
        }
    }

    public void Stop()
    {
        _queue = [];
        _index = -1;
        CurrentTrack = null;
        StreamUrl = null;
        ErrorMessage = null;
        AutoPlay = false;
        Changed?.Invoke();
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _lock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task MoveToAsync(int index)
    {
        _index = index;
        await LoadCurrentAsync(autoPlay: true);
        await SaveCurrentAsync();
    }

    private async Task LoadCurrentAsync(bool autoPlay)
    {
        CurrentTrack = _queue[_index].Track;
        AutoPlay = autoPlay;
        ErrorMessage = null;
        StreamUrl = null;
        Changed?.Invoke();

        try
        {
            StreamUrl = await _apiClient.GetStreamUrlAsync(CurrentTrack.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not play track: {ex.Message}";
        }

        Changed?.Invoke();
    }

    private async Task SaveCurrentAsync()
    {
        if (CurrentEntry is not { IsSaved: true, ItemId: var itemId })
        {
            return;
        }

        try
        {
            await _apiClient.SetQueueCurrentItemAsync(itemId);
        }
        catch
        {
            // Best-effort: losing sync with the server shouldn't block local playback.
        }
    }

    // When nothing is "now playing" (e.g. the first add into an empty queue),
    // load the first entry, paused, so the queue is visible and playable.
    private async Task LoadFirstIfNothingPlayingAsync()
    {
        if (_index >= 0 || _queue.Count == 0)
        {
            return;
        }

        _index = 0;
        await LoadCurrentAsync(autoPlay: false);
        await SaveCurrentAsync();
    }

    // Replaces the local queue with the server's order and "now playing" entry.
    private void Adopt(PlaylistDetailDto queue)
    {
        var items = queue.Items.OrderBy(i => i.Position).ToList();
        _queueId = queue.Id;
        _queue = items.Select(i => new QueueEntry(i.Id, PlaylistTrackMapper.ToMediaItemDto(i.MediaItem))).ToList();
        _index = queue.CurrentItemId is { } currentId ? items.FindIndex(i => i.Id == currentId) : -1;
    }

    private string AddedMessage(List<Guid> ids, bool playNext)
    {
        var what = ids.Count == 1
            ? $"\"{_queue.LastOrDefault(e => e.Track.Id == ids[0])?.Track.Title ?? "Track"}\""
            : $"{ids.Count} tracks";
        return playNext ? $"{what} will play next" : $"Added {what} to the queue";
    }

    private Task HydrateFromServerAsync() => WithLockAsync(async () =>
    {
        try
        {
            Adopt(await _apiClient.GetQueueAsync());
        }
        catch
        {
            // No saved queue yet, or the user isn't signed in: nothing to restore.
            return;
        }

        if (_queue.Count == 0)
        {
            return;
        }

        if (_index < 0)
        {
            // Save the default so a later "Add to next" lands after this entry, not before it.
            _index = 0;
            await SaveCurrentAsync();
        }

        await LoadCurrentAsync(autoPlay: false);
    });
}

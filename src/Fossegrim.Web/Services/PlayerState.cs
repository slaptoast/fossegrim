using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

// One entry in the queue. ItemId is the server's queue entry id, so a track
// queued twice is still two distinct entries. While a newly started queue is
// being saved, entries get a temporary local id (unique, so the Up next list
// can still key on it) and IsSaved is false.
public record QueueEntry(Guid ItemId, MediaItemDto Track, bool IsSaved = true);

// Whether a play started from an album or from tracks: it decides what a
// queue refills itself with (another album, or more tracks).
public enum PlayKind { Track, Album }

// The signed-in user's playback settings (see AppearanceService).
public interface IPlaybackSettings
{
    // Keep playing random picks when the queue runs out.
    bool AutoFillQueue { get; }

    // RandomModes.Full or RandomModes.Adjusted.
    string PreferredRandomMode { get; }
}

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
    private readonly IPlaybackSettings? _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<QueueEntry> _queue = [];
    private int _index = -1;
    private Guid? _queueId;

    // Set while the queue came from a Random module, which always refills
    // (in that module's mode). Other queues refill only with AutoFillQueue on.
    private RandomFill? _random;

    // Albums played this session, so a refill picks a new one while there are any.
    private readonly HashSet<Guid> _playedAlbumIds = [];

    // How many random tracks are queued at a time.
    public const int RandomTrackBatch = 20;

    public PlayerState(FossegrimApiClient apiClient, ToastService toasts, IPlaybackSettings? settings = null)
    {
        _apiClient = apiClient;
        _toasts = toasts;
        _settings = settings;
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

    // How the latest play this session started (null until something is
    // played; a queue restored after a reload counts as tracks).
    public PlayKind? SessionPlayKind { get; private set; }

    public event Action? Changed;

    public Task PlayAsync(MediaItemDto track) => PlayQueueAsync([track]);

    // Replaces the queue with these tracks and starts playing the first.
    public Task PlayQueueAsync(IReadOnlyList<MediaItemDto> tracks) => WithLockAsync(() =>
    {
        _random = null;
        return StartQueueAsync(tracks, PlayKind.Track, albumId: null);
    });

    // Like PlayQueueAsync, for an album's tracks, so a refill continues with albums.
    public Task PlayAlbumAsync(Guid albumId, IReadOnlyList<MediaItemDto> tracks) => WithLockAsync(() =>
    {
        _random = null;
        return StartQueueAsync(tracks, PlayKind.Album, albumId);
    });

    // Plays these tracks followed by RandomTrackBatch more random ones (picked
    // in this mode, none already queued), and keeps topping the queue up
    // whenever its last track starts.
    public Task PlayRandomTracksAsync(IReadOnlyList<MediaItemDto> tracks, string mode) => WithLockAsync(async () =>
    {
        if (tracks.Count == 0)
        {
            return;
        }

        _random = new RandomFill(PlayKind.Track, mode);
        var more = await TryGetRandomTracksAsync(mode, tracks.Select(t => t.Id));
        await StartQueueAsync([.. tracks, .. more], PlayKind.Track, albumId: null);
    });

    // Plays this album, and when its last track starts, queues another
    // random album picked in this mode (one not played yet this session,
    // while there are any).
    public Task PlayRandomAlbumAsync(Guid albumId, IReadOnlyList<MediaItemDto> tracks, string mode) => WithLockAsync(async () =>
    {
        if (tracks.Count == 0)
        {
            return;
        }

        _random = new RandomFill(PlayKind.Album, mode);
        await StartQueueAsync(tracks, PlayKind.Album, albumId);
    });

    private async Task StartQueueAsync(IReadOnlyList<MediaItemDto> tracks, PlayKind kind, Guid? albumId)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        SessionPlayKind = kind;
        if (albumId is { } id)
        {
            _playedAlbumIds.Add(id);
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

        await RefillIfAtEndAsync();
    }

    public Task PlayNextAsync() => WithLockAsync(async () =>
    {
        // Normally the refill already happened when this last track started;
        // this catches one that failed then.
        await RefillIfAtEndAsync();
        if (HasNext)
        {
            await MoveToAsync(_index + 1);
        }
    });

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
        _random = null;
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
        await RefillIfAtEndAsync();
    }

    // Once the last entry is playing, append more random tracks or another
    // random album: always for a queue from a Random module, otherwise when
    // the user has AutoFillQueue on (by how the session's latest play started,
    // in their preferred mode).
    private async Task RefillIfAtEndAsync()
    {
        if (HasNext || CurrentEntry is null)
        {
            return;
        }

        var random = _random ?? (_settings is { AutoFillQueue: true } settings
            ? new RandomFill(SessionPlayKind ?? PlayKind.Track, settings.PreferredRandomMode)
            : null);
        if (random is null)
        {
            return;
        }

        IReadOnlyList<MediaItemDto> more;
        try
        {
            if (random.Kind == PlayKind.Track)
            {
                more = await TryGetRandomTracksAsync(random.Mode, _queue.Select(e => e.Track.Id));
            }
            else
            {
                var album = (await _apiClient.GetRandomAlbumsAsync(random.Mode, 1, _playedAlbumIds)).FirstOrDefault();
                if (album is null)
                {
                    return;
                }

                // Every album has been played this session: start the round again.
                if (_playedAlbumIds.Contains(album.Id))
                {
                    _playedAlbumIds.Clear();
                }
                _playedAlbumIds.Add(album.Id);
                more = await _apiClient.GetAlbumTracksAsync(album.Id);
            }
        }
        catch
        {
            // Best-effort: the queue just ends; the next Next press tries again.
            return;
        }

        if (more.Count == 0)
        {
            return;
        }

        try
        {
            if (_queueId is not { } queueId || _queue.Any(e => !e.IsSaved))
            {
                throw new InvalidOperationException("The queue isn't saved yet.");
            }

            Adopt(await _apiClient.AddPlaylistItemsAsync(queueId, more.Select(t => t.Id)));
        }
        catch
        {
            // Keep playing locally even if the server copy can't be extended.
            _queue.AddRange(more.Select(t => new QueueEntry(Guid.NewGuid(), t, IsSaved: false)));
        }

        Changed?.Invoke();
    }

    // Up to RandomTrackBatch random tracks, leaving out the given ones; none if the request fails.
    private async Task<IReadOnlyList<MediaItemDto>> TryGetRandomTracksAsync(string mode, IEnumerable<Guid> exclude)
    {
        try
        {
            var excluded = exclude.ToHashSet();
            var picked = await _apiClient.GetRandomTracksAsync(mode, RandomTrackBatch, excluded);

            // The server only repeats tracks once there's nothing new left to pick.
            return picked.Where(t => !excluded.Contains(t.Id)).ToList() is { Count: > 0 } fresh ? fresh : picked;
        }
        catch
        {
            return [];
        }
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

// What a queue refills itself with: random tracks or random albums, in this mode.
internal sealed record RandomFill(PlayKind Kind, string Mode);

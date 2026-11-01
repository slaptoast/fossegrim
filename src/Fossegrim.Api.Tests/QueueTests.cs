using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Web.Services;
using Microsoft.JSInterop;

namespace Fossegrim.Api.Tests;

// The queue endpoints, and the Blazor PlayerState that drives them, run
// against the real API (PlayerState talks to it through FossegrimApiClient).
public class QueueTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public QueueTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SetQueue_WithADuplicateTrack_PointsAtTheChosenEntry()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b) = (await _factory.AddMediaItemAsync("A"), await _factory.AddMediaItemAsync("B"));

        var queue = await SetQueueAsync(user, [a.Id, b.Id, a.Id], currentIndex: 2);

        var items = queue.Items.OrderBy(i => i.Position).ToList();
        Assert.Equal(["A", "B", "A"], items.Select(i => i.MediaItem.Title));
        Assert.Equal(items[2].Id, queue.CurrentItemId);
    }

    [Fact]
    public async Task SetQueue_RejectsAnOutOfRangeIndex()
    {
        var user = await _factory.CreateUserAsync();
        var a = await _factory.AddMediaItemAsync();

        var response = await user.Client.PutAsJsonAsync("/api/playlists/queue", new SetQueueRequest([a.Id], 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetCurrent_AcceptsOnlyEntriesInTheQueue()
    {
        var user = await _factory.CreateUserAsync();
        var queue = await SetQueueAsync(user, [(await _factory.AddMediaItemAsync()).Id, (await _factory.AddMediaItemAsync()).Id], 0);
        var second = queue.Items.OrderBy(i => i.Position).Last();

        var ok = await user.Client.PatchAsJsonAsync("/api/playlists/queue/current", new SetQueueCurrentItemRequest(second.Id));
        var bad = await user.Client.PatchAsJsonAsync("/api/playlists/queue/current", new SetQueueCurrentItemRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(second.Id, (await GetQueueAsync(user)).CurrentItemId);
    }

    [Fact]
    public async Task RemovingThePlayingEntry_ClearsThePointer()
    {
        var user = await _factory.CreateUserAsync();
        var queue = await SetQueueAsync(user, [(await _factory.AddMediaItemAsync()).Id], 0);

        await user.Client.DeleteAsync($"/api/playlists/{queue.Id}/items/{queue.CurrentItemId}");

        var after = await GetQueueAsync(user);
        Assert.Empty(after.Items);
        Assert.Null(after.CurrentItemId);
    }

    [Fact]
    public async Task PlaylistList_DoesNotIncludeTheQueue()
    {
        var user = await _factory.CreateUserAsync();
        await SetQueueAsync(user, [(await _factory.AddMediaItemAsync()).Id], 0);
        await user.Client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Road trip"));

        var playlists = await user.Client.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists");

        Assert.Equal(["Road trip"], playlists!.Select(p => p.Name));
    }

    // ---- PlayerState ----

    [Fact]
    public async Task Player_NextAndPrevious_MoveThroughDuplicatesByEntry()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b) = (await TrackAsync("A"), await TrackAsync("B"));
        var player = await CreatePlayerAsync(user);

        await player.PlayQueueAsync([a, b, a]);
        await player.PlayNextAsync();
        await player.PlayNextAsync();

        Assert.Equal(2, player.CurrentIndex);
        Assert.False(player.HasNext);
        var queue = await GetQueueAsync(user);
        Assert.Equal(queue.Items.OrderBy(i => i.Position).Last().Id, queue.CurrentItemId); // the second A, not the first

        await player.PlayPreviousAsync();
        Assert.Equal(1, player.CurrentIndex);
        Assert.Equal("B", player.CurrentTrack!.Title);
        Assert.True(player.AutoPlay);
    }

    [Fact]
    public async Task Player_RestoresTheSameEntryAfterAReload()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b) = (await TrackAsync("A"), await TrackAsync("B"));
        var player = await CreatePlayerAsync(user);
        await player.PlayQueueAsync([a, b, a]);
        await player.PlayNextAsync();
        await player.PlayNextAsync();

        var reloaded = await CreatePlayerAsync(user);

        Assert.Equal(2, reloaded.CurrentIndex);
        Assert.Equal("A", reloaded.CurrentTrack!.Title);
        Assert.False(reloaded.AutoPlay); // a reload doesn't start audio
    }

    [Fact]
    public async Task Player_JumpTo_PlaysThatEntry()
    {
        var user = await _factory.CreateUserAsync();
        var player = await CreatePlayerAsync(user);
        await player.PlayQueueAsync([await TrackAsync("A"), await TrackAsync("B"), await TrackAsync("C")]);

        await player.JumpToAsync(player.Queue[2].ItemId);

        Assert.Equal("C", player.CurrentTrack!.Title);
        Assert.Equal(player.Queue[2].ItemId, (await GetQueueAsync(user)).CurrentItemId);
    }

    [Fact]
    public async Task Player_Remove_TakesAnUpcomingEntryOut()
    {
        var user = await _factory.CreateUserAsync();
        var player = await CreatePlayerAsync(user);
        await player.PlayQueueAsync([await TrackAsync("A"), await TrackAsync("B"), await TrackAsync("C")]);

        await player.RemoveAsync(player.Queue[1].ItemId);

        Assert.Equal(["A", "C"], player.Queue.Select(e => e.Track.Title));
        Assert.Equal(0, player.CurrentIndex);
        Assert.Equal(2, (await GetQueueAsync(user)).Items.Count());
    }

    [Fact]
    public async Task Player_AddNext_GoesAfterThePlayingEntryEvenWhenItsADuplicate()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b, c) = (await TrackAsync("A"), await TrackAsync("B"), await TrackAsync("C"));
        var player = await CreatePlayerAsync(user);
        await player.PlayQueueAsync([a, b, a]);
        await player.PlayNextAsync();
        await player.PlayNextAsync(); // now on the second A

        await player.AddToQueueNextAsync([c.Id]);

        Assert.Equal(["A", "B", "A", "C"], player.Queue.Select(e => e.Track.Title));
        Assert.Equal(2, player.CurrentIndex);
    }

    [Fact]
    public async Task Player_AddingToAnEmptyQueue_LoadsTheFirstTrackPaused()
    {
        var user = await _factory.CreateUserAsync();
        var toasts = new List<string>();
        var player = await CreatePlayerAsync(user, toasts);
        var a = await TrackAsync("A");

        await player.AddToQueueEndAsync([a.Id]);

        Assert.Equal("A", player.CurrentTrack!.Title);
        Assert.False(player.AutoPlay);
        Assert.Equal(player.Queue[0].ItemId, (await GetQueueAsync(user)).CurrentItemId);
        Assert.Equal(["Added \"A\" to the queue"], toasts);
    }

    [Fact]
    public async Task Player_PlayThenImmediatelyAdd_KeepsBoth()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b, c) = (await TrackAsync("A"), await TrackAsync("B"), await TrackAsync("C"));
        // Saving the new queue is slow, so the add starts while it's in flight.
        var player = await CreatePlayerAsync(user, slowQueueSave: true);

        // Not awaited in between: the add must wait for the play to be saved.
        var play = player.PlayQueueAsync([a, b]);
        var add = player.AddToQueueEndAsync([c.Id]);
        await Task.WhenAll(play, add);

        Assert.Equal(["A", "B", "C"], player.Queue.Select(e => e.Track.Title));
        Assert.Equal(["A", "B", "C"], (await GetQueueAsync(user)).Items.OrderBy(i => i.Position).Select(i => i.MediaItem.Title));
        Assert.Equal(0, player.CurrentIndex);
    }

    [Fact]
    public async Task Player_AddSeveral_ShowsACountInTheToast()
    {
        var user = await _factory.CreateUserAsync();
        var toasts = new List<string>();
        var player = await CreatePlayerAsync(user, toasts);
        await player.PlayAsync(await TrackAsync("A"));

        await player.AddToQueueNextAsync([(await TrackAsync("B")).Id, (await TrackAsync("C")).Id]);

        Assert.Equal(["A", "B", "C"], player.Queue.Select(e => e.Track.Title));
        Assert.Equal(["2 tracks will play next"], toasts);
    }

    private async Task<MediaItemDto> TrackAsync(string title)
    {
        var item = await _factory.AddMediaItemAsync(title);
        return new MediaItemDto(item.Id, item.Title, item.ArtistName, item.Album, null, null, null, null, null, item.Duration, null, [], []);
    }

    private async Task<PlayerState> CreatePlayerAsync(TestUser user, List<string>? toasts = null, bool slowQueueSave = false)
    {
        var client = user.Client;
        if (slowQueueSave)
        {
            client = _factory.CreateDefaultClient(new SlowQueueSaveHandler());
            client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        }

        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var api = new FossegrimApiClient(client, new TokenStore(new FakeLocalStorage(token)));
        var toastService = new ToastService();
        if (toasts is not null)
        {
            toastService.Shown += toasts.Add;
        }

        var player = new PlayerState(api, toastService);
        await player.Initialized;
        return player;
    }

    private static async Task<PlaylistDetailDto> SetQueueAsync(TestUser user, List<Guid> ids, int? currentIndex)
    {
        var response = await user.Client.PutAsJsonAsync("/api/playlists/queue", new SetQueueRequest(ids, currentIndex));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaylistDetailDto>())!;
    }

    private static async Task<PlaylistDetailDto> GetQueueAsync(TestUser user) =>
        (await user.Client.GetFromJsonAsync<PlaylistDetailDto>("/api/playlists/queue"))!;

    private sealed class SlowQueueSaveHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/playlists/queue")
            {
                await Task.Delay(300, cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // TokenStore reads the token from localStorage through JS interop.
    internal sealed class FakeLocalStorage(string token) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(identifier == "localStorage.getItem" ? (TValue)(object)token : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

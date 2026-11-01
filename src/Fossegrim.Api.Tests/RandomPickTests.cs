using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

public class RandomPickerTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Weight_IsFullForNeverPlayed_AndLowestJustAfterAPlay()
    {
        Assert.Equal(1, RandomPicker.Weight(null, Now));
        Assert.Equal(RandomPicker.MinWeight, RandomPicker.Weight(Now, Now), 6);
        Assert.Equal(RandomPicker.MinWeight, RandomPicker.Weight(Now.AddMinutes(2), Now), 6);
    }

    [Fact]
    public void Weight_RecoversOverTime()
    {
        Assert.Equal(0.55, RandomPicker.Weight(Now.AddDays(-10), Now), 6);
        Assert.True(RandomPicker.Weight(Now.AddDays(-30), Now) > 0.85);
        Assert.True(RandomPicker.Weight(Now.AddDays(-365), Now) > 0.999);
    }

    [Fact]
    public void Pick_ReturnsDistinctIds_UpToCount()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

        var picked = RandomPicker.Pick(ids.Select(id => (id, 1.0)), 4, new Random(1));

        Assert.Equal(4, picked.Count);
        Assert.Equal(4, picked.Distinct().Count());
        Assert.All(picked, id => Assert.Contains(id, ids));
        Assert.Equal(10, RandomPicker.Pick(ids.Select(id => (id, 1.0)), 50, new Random(1)).Count);
    }

    [Fact]
    public void Pick_FavoursHeavierItems_InProportionToWeight()
    {
        var (fresh, justPlayed) = (Guid.NewGuid(), Guid.NewGuid());
        var random = new Random(42);
        const int trials = 20_000;

        var justPlayedWins = Enumerable.Range(0, trials)
            .Count(_ => RandomPicker.Pick([(fresh, 1.0), (justPlayed, RandomPicker.MinWeight)], 1, random)[0] == justPlayed);

        // 0.1 / (1 + 0.1): rarer, but never ruled out.
        Assert.InRange(justPlayedWins / (double)trials, 0.08, 0.10);
    }

    [Fact]
    public void Pick_WithEqualWeights_IsUniform()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var random = new Random(7);
        const int trials = 20_000;

        var counts = Enumerable.Range(0, trials)
            .Select(_ => RandomPicker.Pick(ids.Select(id => (id, 1.0)), 1, random)[0])
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.All(ids, id => Assert.InRange(counts[id] / (double)trials, 0.23, 0.27));
    }
}

// Each test returns every candidate (count is above the library size), so
// they don't depend on which items the other tests in this class added.
public class RandomEndpointTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public RandomEndpointTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("full")]
    [InlineData("adjusted")]
    public async Task Tracks_ReturnDistinctTracks_UpToCount(string mode)
    {
        var user = await _factory.CreateUserAsync();
        var added = new[] { await _factory.AddMediaItemAsync("One"), await _factory.AddMediaItemAsync("Two"), await _factory.AddMediaItemAsync("Three") };

        var two = await user.Client.GetFromJsonAsync<List<MediaItemDto>>($"/api/random/tracks?mode={mode}&count=2");
        var all = await user.Client.GetFromJsonAsync<List<MediaItemDto>>($"/api/random/tracks?mode={mode}&count=50");

        Assert.Equal(2, two!.Select(t => t.Id).Distinct().Count());
        Assert.All(added, a => Assert.Contains(all!, t => t.Id == a.Id));
    }

    [Fact]
    public async Task Adjusted_StillIncludesAJustPlayedTrack()
    {
        var user = await _factory.CreateUserAsync();
        var played = await _factory.AddMediaItemAsync("Heard it");
        await _factory.WithDbAsync(async db =>
        {
            db.PlayEvents.Add(new PlayEvent { Id = Guid.NewGuid(), UserId = user.UserId, MediaItemId = played.Id, PlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        var tracks = await user.Client.GetFromJsonAsync<List<MediaItemDto>>("/api/random/tracks?mode=adjusted&count=50");

        Assert.Contains(tracks!, t => t.Id == played.Id);
    }

    [Fact]
    public async Task Albums_ReturnAlbumsWithTracks_AndSkipEmptyOnes()
    {
        var user = await _factory.CreateUserAsync();
        var track = await _factory.AddMediaItemAsync(album: "Has tracks", linkedAlbumLabel: "Label");
        var albumId = track.Albums.Single().Id;
        var emptyId = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Albums.Add(new Album { Id = emptyId, Name = "Empty" });
            await db.SaveChangesAsync();
        });

        var albums = await user.Client.GetFromJsonAsync<List<AlbumDto>>("/api/random/albums?count=50");

        Assert.Contains(albums!, a => a.Id == albumId && a.MediaItems.Any(m => m.Id == track.Id));
        Assert.DoesNotContain(albums!, a => a.Id == emptyId);
    }

    [Fact]
    public async Task Post_LeavesOutExcludedTracks_UnlessThatLeavesNone()
    {
        var user = await _factory.CreateUserAsync();
        var (keep, skip) = (await _factory.AddMediaItemAsync("Keep"), await _factory.AddMediaItemAsync("Skip"));
        var allIds = (await user.Client.GetFromJsonAsync<List<MediaItemDto>>("/api/random/tracks?count=50"))!.Select(t => t.Id).ToList();

        var withoutSkip = await PostAsync<MediaItemDto>("/api/random/tracks", user, new RandomPickRequest("full", 50, [skip.Id]));
        var allExcluded = await PostAsync<MediaItemDto>("/api/random/tracks", user, new RandomPickRequest("full", 50, allIds));

        Assert.Contains(withoutSkip, t => t.Id == keep.Id);
        Assert.DoesNotContain(withoutSkip, t => t.Id == skip.Id);
        Assert.NotEmpty(allExcluded);
    }

    [Fact]
    public async Task Post_LeavesOutExcludedAlbums()
    {
        var user = await _factory.CreateUserAsync();
        var (a, b) = (await _factory.AddMediaItemAsync(album: "A", linkedAlbumLabel: "L"), await _factory.AddMediaItemAsync(album: "B", linkedAlbumLabel: "L"));

        var albums = await PostAsync<AlbumDto>("/api/random/albums", user, new RandomPickRequest("adjusted", 50, [a.Albums.Single().Id]));

        Assert.Contains(albums, al => al.Id == b.Albums.Single().Id);
        Assert.DoesNotContain(albums, al => al.Id == a.Albums.Single().Id);
    }

    [Fact]
    public async Task AlbumTracks_AreInTrackOrder()
    {
        var user = await _factory.CreateUserAsync();
        var albumId = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            var album = new Album { Id = albumId, Name = "Ordered" };
            foreach (var (title, number) in new[] { ("Third", 3), ("First", 1), ("Second", 2) })
            {
                db.MediaItems.Add(new MediaItem { Id = Guid.NewGuid(), Title = title, Track = number, FileLocation = $"/music/{Guid.NewGuid():N}.mp3", Albums = [album] });
            }
            await db.SaveChangesAsync();
        });

        var tracks = await user.Client.GetFromJsonAsync<List<MediaItemDto>>($"/api/albums/{albumId}/tracks");
        var missing = await user.Client.GetAsync($"/api/albums/{Guid.NewGuid()}/tracks");

        Assert.Equal(["First", "Second", "Third"], tracks!.Select(t => t.Title));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<List<T>> PostAsync<T>(string url, TestUser user, RandomPickRequest request)
    {
        var response = await user.Client.PostAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<T>>())!;
    }

    [Theory]
    [InlineData("/api/random/tracks?mode=shuffle")]
    [InlineData("/api/random/albums?mode=shuffle")]
    public async Task UnknownMode_IsRejected(string url)
    {
        var user = await _factory.CreateUserAsync();

        var response = await user.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequiresSignIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/random/tracks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// PlayerState refilling a queue started from a Random module. Each test
// gets its own library so its size is known.
public class RandomPlaybackTests : IAsyncLifetime
{
    private readonly FossegrimApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task RandomTrack_QueuesTwentyMoreNewTracks_ThenTopsUpAtTheEnd()
    {
        var user = await _factory.CreateUserAsync();
        var tracks = new List<MediaItemDto>();
        for (var i = 0; i < 25; i++)
        {
            tracks.Add(ToDto(await _factory.AddMediaItemAsync($"T{i}")));
        }
        var player = await CreatePlayerAsync(user);

        await player.PlayRandomTracksAsync([tracks[0]], RandomModes.Adjusted);

        Assert.Equal(1 + PlayerState.RandomTrackBatch, player.Queue.Count);
        Assert.Equal(tracks[0].Id, player.CurrentTrack!.Id);
        Assert.Equal(player.Queue.Count, player.Queue.Select(e => e.Track.Id).Distinct().Count());

        // Reaching the last track queues the 4 tracks not played yet.
        while (player.CurrentIndex < PlayerState.RandomTrackBatch)
        {
            await player.PlayNextAsync();
        }
        Assert.Equal(25, player.Queue.Count);
        Assert.Equal(25, player.Queue.Select(e => e.Track.Id).Distinct().Count());
        Assert.Equal(25, (await GetQueueAsync(user)).Items.Count());

        // With everything queued, it starts over rather than stopping.
        while (player.CurrentIndex < 24)
        {
            await player.PlayNextAsync();
        }
        Assert.Equal(25 + PlayerState.RandomTrackBatch, player.Queue.Count);
        Assert.True(player.HasNext);
    }

    [Fact]
    public async Task RandomAlbum_IsFollowedByOtherAlbums_ThenStartsOver()
    {
        var user = await _factory.CreateUserAsync();
        var albumIds = new List<Guid>();
        var firstTracks = new List<MediaItemDto>();
        for (var i = 0; i < 3; i++)
        {
            var albumId = Guid.NewGuid();
            albumIds.Add(albumId);
            await _factory.WithDbAsync(async db =>
            {
                var album = new Album { Id = albumId, Name = $"Album {i}" };
                for (var n = 1; n <= 2; n++)
                {
                    var item = new MediaItem { Id = Guid.NewGuid(), Title = $"A{i}-{n}", Track = n, FileLocation = $"/music/{Guid.NewGuid():N}.mp3", Albums = [album] };
                    db.MediaItems.Add(item);
                    if (i == 0)
                    {
                        firstTracks.Add(ToDto(item));
                    }
                }
                await db.SaveChangesAsync();
            });
        }
        var player = await CreatePlayerAsync(user);

        await player.PlayRandomAlbumAsync(albumIds[0], firstTracks, RandomModes.Full);
        Assert.Equal(2, player.Queue.Count);

        await player.PlayNextAsync(); // last track of album 0: another album is queued
        await player.PlayNextAsync();
        await player.PlayNextAsync(); // last track of the second album: the third is queued

        var albumsInOrder = player.Queue.Select(e => e.Track.Title![..2]).Distinct().ToList();
        Assert.Equal(6, player.Queue.Count);
        Assert.Equal(3, albumsInOrder.Count);
        Assert.Equal("A0", albumsInOrder[0]);

        // Every album has been played: the next one may be any of them.
        await player.PlayNextAsync();
        await player.PlayNextAsync();
        Assert.Equal(8, player.Queue.Count);
    }

    [Fact]
    public async Task OrdinaryPlay_StopsTheRandomRefill()
    {
        var user = await _factory.CreateUserAsync();
        var tracks = new List<MediaItemDto>();
        for (var i = 0; i < 3; i++)
        {
            tracks.Add(ToDto(await _factory.AddMediaItemAsync($"T{i}")));
        }
        var player = await CreatePlayerAsync(user);

        await player.PlayRandomTracksAsync([tracks[0]], RandomModes.Full);
        await player.PlayQueueAsync([tracks[1]]);
        await player.PlayNextAsync();

        Assert.Single(player.Queue);
        Assert.False(player.HasNext);
    }

    [Fact]
    public async Task AutoFillOff_AnOrdinaryQueueJustEnds()
    {
        var user = await _factory.CreateUserAsync();
        var (albumId, tracks) = await AddAlbumAsync("Only", 2);
        await AddAlbumAsync("Other", 2);
        var player = await CreatePlayerAsync(user, new Settings(AutoFillQueue: false));

        await player.PlayAlbumAsync(albumId, tracks);
        await player.PlayNextAsync();

        Assert.Equal(2, player.Queue.Count);
        Assert.False(player.HasNext);
        Assert.Equal(PlayKind.Album, player.SessionPlayKind);
    }

    [Fact]
    public async Task AutoFillOn_AfterAnAlbum_QueuesAnotherAlbum()
    {
        var user = await _factory.CreateUserAsync();
        var (albumId, tracks) = await AddAlbumAsync("First", 2);
        var (_, other) = await AddAlbumAsync("Second", 3);
        var player = await CreatePlayerAsync(user, new Settings(AutoFillQueue: true));

        await player.PlayAlbumAsync(albumId, tracks);
        await player.PlayNextAsync();

        // The album played this session is skipped, so the other one follows, in track order.
        Assert.Equal([.. tracks.Select(t => t.Id), .. other.Select(t => t.Id)], player.Queue.Select(e => e.Track.Id));
    }

    [Fact]
    public async Task AutoFillOn_AfterATrack_QueuesMoreTracks()
    {
        var user = await _factory.CreateUserAsync();
        var tracks = new List<MediaItemDto>();
        for (var i = 0; i < 5; i++)
        {
            tracks.Add(ToDto(await _factory.AddMediaItemAsync($"T{i}")));
        }
        var player = await CreatePlayerAsync(user, new Settings(AutoFillQueue: true));

        await player.PlayAsync(tracks[0]);

        Assert.Equal(PlayKind.Track, player.SessionPlayKind);
        Assert.Equal(5, player.Queue.Count); // the played track plus the 4 others, no repeats
        Assert.Equal(5, player.Queue.Select(e => e.Track.Id).Distinct().Count());
        Assert.Equal(tracks[0].Id, player.CurrentTrack!.Id);
    }

    private async Task<(Guid AlbumId, List<MediaItemDto> Tracks)> AddAlbumAsync(string name, int trackCount)
    {
        var albumId = Guid.NewGuid();
        var tracks = new List<MediaItemDto>();
        await _factory.WithDbAsync(async db =>
        {
            var album = new Album { Id = albumId, Name = name };
            for (var n = 1; n <= trackCount; n++)
            {
                var item = new MediaItem { Id = Guid.NewGuid(), Title = $"{name} {n}", Track = n, FileLocation = $"/music/{Guid.NewGuid():N}.mp3", Albums = [album] };
                db.MediaItems.Add(item);
                tracks.Add(ToDto(item));
            }
            await db.SaveChangesAsync();
        });
        return (albumId, tracks);
    }

    private sealed record Settings(bool AutoFillQueue, string PreferredRandomMode = RandomModes.Adjusted) : IPlaybackSettings;

    private static MediaItemDto ToDto(MediaItem item) =>
        new(item.Id, item.Title, item.ArtistName, item.Album, null, null, null, item.Track, null, item.Duration, null, [], []);

    private async Task<PlayerState> CreatePlayerAsync(TestUser user, IPlaybackSettings? settings = null)
    {
        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var player = new PlayerState(new FossegrimApiClient(user.Client, new TokenStore(new QueueTests.FakeLocalStorage(token))), new ToastService(), settings);
        await player.Initialized;
        return player;
    }

    private static async Task<PlaylistDetailDto> GetQueueAsync(TestUser user) =>
        (await user.Client.GetFromJsonAsync<PlaylistDetailDto>("/api/playlists/queue"))!;
}

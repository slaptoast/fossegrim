using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Tests;

public class PlaysEndpointTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public PlaysEndpointTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Record_ReturnsCreatedPlay_WithLocationThatResolves()
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync(title: "Teardrop", artist: "José González");

        var response = await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var play = await response.Content.ReadFromJsonAsync<PlayEventDto>();
        Assert.Equal(track.Id, play!.MediaItem.Id);
        Assert.Equal("Teardrop", play.MediaItem.Title);
        Assert.Equal("José González", play.MediaItem.ArtistName);

        var fetched = await client.GetFromJsonAsync<PlayEventDto>(response.Headers.Location);
        Assert.Equal(play.Id, fetched!.Id);
    }

    [Fact]
    public async Task Record_StoresClientStartTimeAndListenedFor()
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-3);

        var response = await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id, startedAt, TimeSpan.FromSeconds(150)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var play = await response.Content.ReadFromJsonAsync<PlayEventDto>();
        Assert.Equal(startedAt.UtcDateTime, play!.PlayedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromSeconds(150), play.ListenedFor);

        await _factory.WithDbAsync(async db =>
        {
            var stored = await db.PlayEvents.SingleAsync(p => p.Id == play.Id);
            Assert.True(stored.RecordedAt > stored.PlayedAt);
            Assert.Null(stored.ScrobbledAt);
        });
    }

    [Fact]
    public async Task Record_WithoutStartTime_UsesServerTime()
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();

        var response = await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        var play = await response.Content.ReadFromJsonAsync<PlayEventDto>();
        Assert.Equal(DateTime.UtcNow, play!.PlayedAt, TimeSpan.FromMinutes(1));
        Assert.Null(play.ListenedFor);
    }

    [Fact]
    public async Task Record_UnknownTrack_ReturnsNotFound()
    {
        var client = await _factory.CreateUserClientAsync();

        var response = await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Record_MissingMediaItemId_ReturnsBadRequest()
    {
        var client = await _factory.CreateUserClientAsync();

        var response = await client.PostAsJsonAsync("/api/plays", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public static TheoryData<int?, int?> InvalidTimings => new()
    {
        // startedAt offset from now (minutes), listenedFor (seconds)
        { 10, null },     // starts in the future
        { -3, -1 },       // negative listening time
        { -1, 600 },      // heard 10 minutes of a play that started 1 minute ago
    };

    [Theory]
    [MemberData(nameof(InvalidTimings))]
    public async Task Record_ImpossibleTiming_ReturnsBadRequest(int? startedMinutesFromNow, int? listenedSeconds)
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        var request = new RecordPlayRequest(
            track.Id,
            startedMinutesFromNow is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null,
            listenedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        var response = await client.PostAsJsonAsync("/api/plays", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/plays")]
    [InlineData("POST", "/api/plays")]
    [InlineData("GET", "/api/plays/00000000-0000-0000-0000-000000000001")]
    public async Task Endpoints_RequireSignIn(string method, string url)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new RecordPlayRequest(Guid.NewGuid())) : null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task History_IsNewestFirstByStartTime()
    {
        var client = await _factory.CreateUserClientAsync();
        var first = await _factory.AddMediaItemAsync(title: "First");
        var second = await _factory.AddMediaItemAsync(title: "Second");
        var now = DateTimeOffset.UtcNow;

        // Reported out of order: the later play arrives first.
        await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(second.Id, now.AddMinutes(-5)));
        await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(first.Id, now.AddMinutes(-10)));

        var history = await client.GetFromJsonAsync<List<PlayEventDto>>("/api/plays");

        Assert.Equal(["Second", "First"], history!.Select(p => p.MediaItem.Title));
    }

    [Theory]
    [InlineData("?limit=1", 1)]
    [InlineData("?limit=0", 1)]   // clamped up to 1
    [InlineData("?limit=999", 3)] // clamped to the maximum, which is more than exist
    [InlineData("", 3)]
    public async Task History_HonorsLimit(string query, int expectedCount)
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));
        }

        var history = await client.GetFromJsonAsync<List<PlayEventDto>>($"/api/plays{query}");

        Assert.Equal(expectedCount, history!.Count);
    }

    [Fact]
    public async Task History_InvalidLimit_ReturnsBadRequest()
    {
        var client = await _factory.CreateUserClientAsync();

        var response = await client.GetAsync("/api/plays?limit=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Plays_AreVisibleOnlyToTheirOwner()
    {
        var owner = await _factory.CreateUserClientAsync();
        var other = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();

        var response = await owner.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        Assert.Empty((await other.GetFromJsonAsync<List<PlayEventDto>>("/api/plays"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(response.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task DeletingTrack_DeletesItsPlays()
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        await _factory.WithDbAsync(db => db.MediaItems.Where(m => m.Id == track.Id).ExecuteDeleteAsync());

        Assert.Empty((await client.GetFromJsonAsync<List<PlayEventDto>>("/api/plays"))!);
    }
}

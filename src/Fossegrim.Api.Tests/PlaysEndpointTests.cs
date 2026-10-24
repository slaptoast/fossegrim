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

        var history = (await GetHistoryAsync(client)).Items;

        Assert.Equal(["Second", "First"], history!.Select(p => p.MediaItem.Title));
    }

    [Theory]
    [InlineData("?pageSize=1", 1)]
    [InlineData("?pageSize=0", 1)]   // clamped up to 1
    [InlineData("?pageSize=999", 3)] // clamped to the maximum, which is more than exist
    [InlineData("", 3)]
    public async Task History_HonorsPageSize(string query, int expectedCount)
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));
        }

        var history = await GetHistoryAsync(client, query);

        Assert.Equal(expectedCount, history.Items.Count);
        Assert.Equal(3, history.TotalCount);
    }

    [Fact]
    public async Task History_PagesThroughInOrder()
    {
        var client = await _factory.CreateUserClientAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var (title, minutesAgo) in new[] { ("A", 1), ("B", 2), ("C", 3), ("D", 4), ("E", 5) })
        {
            var track = await _factory.AddMediaItemAsync(title: title);
            await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id, now.AddMinutes(-minutesAgo)));
        }

        var page2 = await GetHistoryAsync(client, "?page=2&pageSize=2");
        var page3 = await GetHistoryAsync(client, "?page=3&pageSize=2");

        Assert.Equal(["C", "D"], page2.Items.Select(p => p.MediaItem.Title));
        Assert.Equal(["E"], page3.Items.Select(p => p.MediaItem.Title));
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page2.Page);
    }

    public static TheoryData<string, string> SearchCases => new()
    {
        // query, the one title it should find
        { "teardrop", "Teardrop" },          // title, case-insensitive
        { "massive", "Teardrop" },           // artist tag
        { "mezzanine", "Teardrop" },         // album tag
        { "trip-hop", "Teardrop" },          // genre
        { "1998", "Teardrop" },              // year
        { "3D", "Teardrop" },                // linked artist name
        { "virgin", "Teardrop" },            // linked album's label
        { "massive 1998", "Teardrop" },      // every word must match, across tags
        { "100%", "100% Pure" },             // % is matched literally, not as a wildcard
    };

    [Theory]
    [MemberData(nameof(SearchCases))]
    public async Task History_SearchMatchesAnyTag(string query, string expectedTitle)
    {
        var client = await _factory.CreateUserClientAsync();
        await PlayAsync(client, await _factory.AddMediaItemAsync(
            title: "Teardrop", artist: "Massive Attack", album: "Mezzanine", genre: "Trip-Hop", year: 1998,
            linkedArtistName: "3D", linkedAlbumLabel: "Virgin"));
        await PlayAsync(client, await _factory.AddMediaItemAsync(
            title: "100% Pure", artist: "Someone Else", album: "Other", genre: "Pop", year: 2001));
        await PlayAsync(client, await _factory.AddMediaItemAsync(
            title: "Unrelated", artist: "Nobody", album: "Nothing", genre: "Jazz", year: 1970));

        var history = await GetHistoryAsync(client, $"?q={Uri.EscapeDataString(query)}");

        var play = Assert.Single(history.Items);
        Assert.Equal(expectedTitle, play.MediaItem.Title);
        Assert.Equal(1, history.TotalCount);
    }

    [Fact]
    public async Task History_SearchWithNoMatches_IsEmpty()
    {
        var client = await _factory.CreateUserClientAsync();
        await PlayAsync(client, await _factory.AddMediaItemAsync(title: "Teardrop"));

        var history = await GetHistoryAsync(client, "?q=teardrop%20zzz");

        Assert.Empty(history.Items);
        Assert.Equal(0, history.TotalCount);
    }

    [Fact]
    public async Task History_InvalidPage_ReturnsBadRequest()
    {
        var client = await _factory.CreateUserClientAsync();

        var response = await client.GetAsync("/api/plays?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Plays_AreVisibleOnlyToTheirOwner()
    {
        var owner = await _factory.CreateUserClientAsync();
        var other = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();

        var response = await owner.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        Assert.Empty(((await GetHistoryAsync(other)).Items)!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(response.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task DeletingTrack_DeletesItsPlays()
    {
        var client = await _factory.CreateUserClientAsync();
        var track = await _factory.AddMediaItemAsync();
        await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id));

        await _factory.WithDbAsync(db => db.MediaItems.Where(m => m.Id == track.Id).ExecuteDeleteAsync());

        Assert.Empty(((await GetHistoryAsync(client)).Items)!);
    }

    private static async Task PlayAsync(HttpClient client, Fossegrim.Lib.Models.MediaItem track) =>
        (await client.PostAsJsonAsync("/api/plays", new RecordPlayRequest(track.Id))).EnsureSuccessStatusCode();

    private static async Task<PlayHistoryPageDto> GetHistoryAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PlayHistoryPageDto>($"/api/plays{query}"))!;
}

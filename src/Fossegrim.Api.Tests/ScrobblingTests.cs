using System.Net;
using System.Net.Http.Json;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

// A fresh API per test: the scrobbler processes every user's due plays, so
// tests sharing a database would see each other's leftovers.
public sealed class ScrobblingTests : IDisposable
{
    private readonly FossegrimApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task EligiblePlay_ForLinkedUser_IsScrobbledWithItsStartTime()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync("session-1");
        var track = await _factory.AddMediaItemAsync(title: "Teardrop", artist: "Massive Attack", album: "Mezzanine");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-4);

        var play = await RecordPlayAsync(user, track, startedAt);
        Assert.Equal("Pending", play.ScrobbleStatus);

        await ProcessDueAsync(DateTime.UtcNow);

        var stored = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Scrobbled, stored.ScrobbleStatus);
        Assert.NotNull(stored.ScrobbledAt);
        Assert.Null(stored.NextScrobbleAttemptAt);

        var scrobble = Assert.Single(_factory.LastFm.Scrobbles);
        Assert.Equal("Massive Attack", scrobble["artist"]);
        Assert.Equal("Teardrop", scrobble["track"]);
        Assert.Equal("Mezzanine", scrobble["album"]);
        Assert.Equal("session-1", scrobble["sk"]);
        Assert.Equal(startedAt.ToUnixTimeSeconds().ToString(), scrobble["timestamp"]);
    }

    [Fact]
    public async Task Listens_ShowScrobbleStatus()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        await ProcessDueAsync(DateTime.UtcNow);

        var history = await user.Client.GetFromJsonAsync<PlayHistoryPageDto>("/api/plays");
        var listed = Assert.Single(history!.Items);
        Assert.Equal(play.Id, listed.Id);
        Assert.Equal("Scrobbled", listed.ScrobbleStatus);
    }

    public static TheoryData<int?> UnheardListens => new()
    {
        20,   // 20s of a 3-minute track: a skip
        90,   // half: Last.fm's own minimum, but under our 90%
        150,  // 83%
        null, // a client that didn't say how long it played
    };

    [Theory]
    [MemberData(nameof(UnheardListens))]
    public async Task PlayNotMeetingTheRule_IsNotScrobbled(int? listenedSeconds)
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();

        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync(), startedAt: null,
            listenedFor: listenedSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
        await ProcessDueAsync(DateTime.UtcNow);

        Assert.Null(play.ScrobbleStatus);
        Assert.Empty(_factory.LastFm.Scrobbles);
    }

    [Fact]
    public async Task PlayByUnlinkedUser_IsNotScrobbled()
    {
        ConfigureLastFm();
        var user = await _factory.CreateUserAsync();

        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());
        await ProcessDueAsync(DateTime.UtcNow);

        Assert.Null(play.ScrobbleStatus);
        Assert.Empty(_factory.LastFm.Requests);
    }

    [Fact]
    public async Task PlayWhileLastFmIsOff_IsNotScrobbled()
    {
        var user = await _factory.CreateUserAsync();
        await LinkAsync(user, "session");

        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        Assert.Null(play.ScrobbleStatus);
    }

    [Fact]
    public async Task TemporaryFailures_AreRetriedWithBackoff_ThenSucceed()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());
        var now = DateTime.UtcNow;

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.Error(11, "Service Offline"));
        await ProcessDueAsync(now);
        var afterFirst = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Pending, afterFirst.ScrobbleStatus);
        Assert.Equal(1, afterFirst.ScrobbleAttempts);
        Assert.Equal(now.AddMinutes(1), afterFirst.NextScrobbleAttemptAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("Service Offline", afterFirst.ScrobbleError);

        // Not due yet: nothing is sent.
        await ProcessDueAsync(now.AddSeconds(30));
        Assert.Single(_factory.LastFm.Scrobbles);

        _factory.LastFm.Respond = _ => (HttpStatusCode.ServiceUnavailable, "<html>down</html>");
        await ProcessDueAsync(now.AddMinutes(1));
        var afterSecond = await GetPlayAsync(play.Id);
        Assert.Equal(2, afterSecond.ScrobbleAttempts);
        Assert.Equal(now.AddMinutes(6), afterSecond.NextScrobbleAttemptAt!.Value, TimeSpan.FromSeconds(1));

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.AcceptedScrobble);
        await ProcessDueAsync(now.AddMinutes(6));
        var final = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Scrobbled, final.ScrobbleStatus);
        Assert.Null(final.ScrobbleError);
        Assert.Equal(3, _factory.LastFm.Scrobbles.Count());
    }

    [Fact]
    public async Task NetworkError_IsRetried()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        _factory.LastFm.Respond = _ => throw new HttpRequestException("Connection refused");
        await ProcessDueAsync(DateTime.UtcNow);

        var stored = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Pending, stored.ScrobbleStatus);
        Assert.Equal("Connection refused", stored.ScrobbleError);
    }

    [Fact]
    public async Task IgnoredScrobble_IsMarkedIgnored()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.IgnoredScrobble(1, "Artist was ignored"));
        await ProcessDueAsync(DateTime.UtcNow);

        var stored = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Ignored, stored.ScrobbleStatus);
        Assert.Equal("Artist was ignored", stored.ScrobbleError);
    }

    [Fact]
    public async Task DailyLimit_IsRetriedLater()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.IgnoredScrobble(5, "Daily scrobble limit exceeded"));
        await ProcessDueAsync(DateTime.UtcNow);

        Assert.Equal(ScrobbleStatus.Pending, (await GetPlayAsync(play.Id)).ScrobbleStatus);
    }

    [Fact]
    public async Task PermanentError_GivesUp()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.Error(6, "Invalid parameters"));
        await ProcessDueAsync(DateTime.UtcNow);

        var stored = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Failed, stored.ScrobbleStatus);
        Assert.Contains("Invalid parameters", stored.ScrobbleError);
        Assert.Null(stored.NextScrobbleAttemptAt);
    }

    [Fact]
    public async Task RevokedSession_UnlinksAndFailsTheUsersOtherPlays()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var first = await RecordPlayAsync(user, await _factory.AddMediaItemAsync(), DateTimeOffset.UtcNow.AddMinutes(-10));
        var second = await RecordPlayAsync(user, await _factory.AddMediaItemAsync(), DateTimeOffset.UtcNow.AddMinutes(-5));

        _factory.LastFm.Respond = _ => (HttpStatusCode.OK, FakeLastFmHandler.Error(9, "Invalid session key"));
        await ProcessDueAsync(DateTime.UtcNow);

        Assert.Equal(ScrobbleStatus.Failed, (await GetPlayAsync(first.Id)).ScrobbleStatus);
        Assert.Equal(ScrobbleStatus.Failed, (await GetPlayAsync(second.Id)).ScrobbleStatus);
        Assert.Single(_factory.LastFm.Scrobbles); // the second play wasn't sent with the dead key

        var status = await user.Client.GetFromJsonAsync<LastFmStatusDto>("/api/lastfm");
        Assert.False(status!.Connected);
    }

    [Fact]
    public async Task PlayOlderThan14Days_IsDroppedWithoutSending()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        // Last.fm was down the whole time; by now it's too late.
        await ProcessDueAsync(DateTime.UtcNow.AddDays(15));

        var stored = await GetPlayAsync(play.Id);
        Assert.Equal(ScrobbleStatus.Failed, stored.ScrobbleStatus);
        Assert.Contains("14 days", stored.ScrobbleError);
        Assert.Empty(_factory.LastFm.Scrobbles);
    }

    [Fact]
    public async Task UserWhoUnlinksBeforeSending_IsNotScrobbled()
    {
        ConfigureLastFm();
        var user = await CreateLinkedUserAsync();
        var play = await RecordPlayAsync(user, await _factory.AddMediaItemAsync());

        await user.Client.DeleteAsync("/api/lastfm/session");
        await ProcessDueAsync(DateTime.UtcNow);

        Assert.Equal(ScrobbleStatus.Failed, (await GetPlayAsync(play.Id)).ScrobbleStatus);
        Assert.Empty(_factory.LastFm.Scrobbles);
    }

    private void ConfigureLastFm() =>
        _factory.Services.GetRequiredService<LastFmCredentialsHolder>()
            .Set(new LastFmCredentials("test-key", "test-secret", LastFmCredentialSource.Settings));

    private async Task<TestUser> CreateLinkedUserAsync(string sessionKey = "session")
    {
        var user = await _factory.CreateUserAsync();
        await LinkAsync(user, sessionKey);
        return user;
    }

    private async Task LinkAsync(TestUser user, string sessionKey)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var accounts = scope.ServiceProvider.GetRequiredService<LastFmAccountStore>();
        await accounts.SetAsync((await userManager.FindByIdAsync(user.UserId))!, sessionKey, "lastfm-user");
    }

    // A 3-minute track (from AddMediaItemAsync) heard for 2:45, over 90%: enough to scrobble.
    private static Task<PlayEventDto> RecordPlayAsync(TestUser user, MediaItem track, DateTimeOffset? startedAt = null) =>
        RecordPlayAsync(user, track, startedAt, TimeSpan.FromSeconds(165));

    private static async Task<PlayEventDto> RecordPlayAsync(TestUser user, MediaItem track, DateTimeOffset? startedAt, TimeSpan? listenedFor)
    {
        var request = new RecordPlayRequest(track.Id, startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-3), listenedFor);
        var response = await user.Client.PostAsJsonAsync("/api/plays", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlayEventDto>())!;
    }

    private async Task ProcessDueAsync(DateTime utcNow)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Scrobbler>().ProcessDueAsync(utcNow);
    }

    private async Task<PlayEvent> GetPlayAsync(Guid id)
    {
        PlayEvent? play = null;
        await _factory.WithDbAsync(async db => play = await db.PlayEvents.AsNoTracking().SingleAsync(p => p.Id == id));
        return play!;
    }
}

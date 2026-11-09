using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fossegrim.Api.Tests;

/// <summary>
/// Password guessing limits, media tokens, browser security headers, backup
/// file permissions, display name rules and playlist existence checks.
/// </summary>
public class SecurityTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public SecurityTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    // --- Lockout ---

    [Fact]
    public async Task TooManyWrongPasswords_LockTheAccount_EvenForTheRightPassword()
    {
        var user = await _factory.CreateUserAsync();
        var client = _factory.CreateClient();

        for (var i = 1; i < SignInLimits.MaxFailedAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, user.Email, "wrong")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, user.Email, "wrong")).StatusCode);

        var right = await LoginAsync(client, user.Email, user.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Contains("Try again in", await right.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SigningIn_ResetsTheWrongPasswordCount()
    {
        var user = await _factory.CreateUserAsync();
        var client = _factory.CreateClient();

        for (var round = 0; round < 2; round++)
        {
            for (var i = 1; i < SignInLimits.MaxFailedAttempts; i++)
            {
                await LoginAsync(client, user.Email, "wrong");
            }
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, user.Email, user.Password)).StatusCode);
        }
    }

    [Fact]
    public async Task UnknownEmails_AreRefusedLikeWrongPasswords()
    {
        var response = await LoginAsync(_factory.CreateClient(), $"nobody-{Guid.NewGuid():N}@example.com", "whatever");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Media tokens ---

    [Fact]
    public async Task MediaTokens_OpenStreamAndCoverUrls_ButNotTheApi()
    {
        var login = await SignInAsync(await _factory.CreateAdminAsync());
        var mediaToken = Assert.IsType<string>(login.MediaToken);
        var anonymous = _factory.CreateClient();

        // Authenticated: an unknown id is a 404 rather than a 401.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/cover/{Guid.NewGuid()}?access_token={mediaToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/stream/{Guid.NewGuid()}?access_token={mediaToken}")).StatusCode);

        var withMediaToken = ClientWith(mediaToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withMediaToken.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withMediaToken.GetAsync("/api/admin/folders")).StatusCode);
    }

    [Fact]
    public async Task AccessTokens_AreNotAcceptedInUrls_ButWorkAsHeadersOnMedia()
    {
        var login = await SignInAsync(await _factory.CreateUserAsync());

        var inUrl = await _factory.CreateClient().GetAsync($"/cover/{Guid.NewGuid()}?access_token={login.Token}");
        var inHeader = await ClientWith(login.Token).GetAsync($"/stream/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, inUrl.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inHeader.StatusCode);
    }

    [Fact]
    public async Task RefreshingAlsoIssuesAMediaToken()
    {
        var login = await SignInAsync(await _factory.CreateUserAsync());

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken!));
        var refreshed = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.NotNull(refreshed!.MediaToken);
        Assert.True(refreshed.MediaTokenExpiresAt > refreshed.ExpiresAt);
    }

    // --- Headers ---

    [Fact]
    public async Task Responses_CarrySecurityHeaders()
    {
        var response = await _factory.CreateClient().GetAsync("/api/version");

        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public void ScriptPolicy_AllowsEvalForTheVisualizer_ButOnlyTheAppsOwnScripts()
    {
        // Butterchurn compiles MilkDrop presets with new Function(); without
        // 'unsafe-eval' the visualizer never gets past "Loading".
        var scriptSrc = SecurityHeaders.ContentSecurityPolicy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src "));

        Assert.Equal("script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'", scriptSrc);
    }

    [Fact]
    public async Task Cors_AllowsNoOtherOriginsOutsideDevelopment()
    {
        var client = await _factory.CreateUserClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Origin", "http://localhost:5085");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // --- Backups ---

    [Fact]
    public async Task Backups_AreReadableByTheirOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var admin = await _factory.CreateAdminAsync();
        var response = await admin.Client.PostAsync("/api/admin/backups", null);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<BackupStatusDto>();

        var path = Path.Combine(_factory.BackupDirectory, status!.LastRun!.FileName!);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // --- Display names ---

    [Fact]
    public async Task DisplayNames_CantCopyAnotherAccounts()
    {
        var admin = await _factory.CreateAdminAsync();
        var adminName = $"Admin {Guid.NewGuid():N}";
        (await admin.Client.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest(adminName, null))).EnsureSuccessStatusCode();
        var user = await _factory.CreateUserAsync();

        var copy = await user.Client.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest($"  {adminName.ToUpperInvariant()} ", null));
        // Keeping your own name is fine.
        var same = await admin.Client.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest(adminName, null));

        Assert.Equal(HttpStatusCode.BadRequest, copy.StatusCode);
        Assert.Contains("already uses", (await copy.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Theory]
    [InlineData(101, null)]
    [InlineData(0, "not a number")]
    [InlineData(0, "0123456789012345678901234567890")]
    public async Task Profiles_RejectOverlongNamesAndBadPhoneNumbers(int nameLength, string? phoneNumber)
    {
        var user = await _factory.CreateUserAsync();
        var name = nameLength > 0 ? new string('x', nameLength) : null;

        var response = await user.Client.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest(name, phoneNumber));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminsCantCreateAccountsWithATakenDisplayName()
    {
        var admin = await _factory.CreateAdminAsync();
        var name = $"Person {Guid.NewGuid():N}";
        var first = await admin.Client.PostAsJsonAsync("/api/admin/users", new CreateUserRequest($"a-{Guid.NewGuid():N}@example.com", name, "Passw0rd!x", false));

        var second = await admin.Client.PostAsJsonAsync("/api/admin/users", new CreateUserRequest($"b-{Guid.NewGuid():N}@example.com", name, "Passw0rd!x", false));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task TheDatabaseRefusesDuplicateDisplayNames_HoweverTheyreSaved()
    {
        var first = await _factory.CreateUserAsync();
        var second = await _factory.CreateUserAsync();
        var name = $"Twin {Guid.NewGuid():N}";
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(first.UserId))!.DisplayName = name;
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(second.UserId))!.DisplayName = $" {name.ToLowerInvariant()} ";
            await db.SaveChangesAsync();
        }));
    }

    // Installs from before names were unique may have duplicates; startup
    // renames all but the earliest account's.
    [Fact]
    public async Task StartupRenamesExistingDuplicates_KeepingTheEarliestAccountsName()
    {
        var earlier = await _factory.CreateUserAsync();
        var later = await _factory.CreateUserAsync();
        var third = await _factory.CreateUserAsync();
        var name = $"Tom {Guid.NewGuid():N}";
        await _factory.WithDbAsync(async db =>
        {
            // As the rows were before the unique index: no normalized name.
            foreach (var (id, joined, displayName) in new[] { (earlier.UserId, 1, name), (later.UserId, 2, name.ToUpperInvariant()), (third.UserId, 3, $"{name} 2") })
            {
                await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.DisplayName, displayName)
                    .SetProperty(u => u.NormalizedDisplayName, (string?)null)
                    .SetProperty(u => u.DateJoined, new DateTime(2020, 1, joined, 0, 0, 0, DateTimeKind.Utc)));
            }
        });

        await _factory.WithDbAsync(db => DisplayNames.EnsureUniqueAsync(db, NullLogger.Instance));

        await _factory.WithDbAsync(async db =>
        {
            var names = await db.Users.AsNoTracking()
                .Where(u => u.Id == earlier.UserId || u.Id == later.UserId || u.Id == third.UserId)
                .ToDictionaryAsync(u => u.Id, u => (u.DisplayName, u.NormalizedDisplayName));
            Assert.Equal((name, name.ToUpperInvariant()), names[earlier.UserId]);
            // "Tom 2" was already someone's, so the second Tom is "Tom 3".
            Assert.Equal(($"{name} 2", $"{name} 2".ToUpperInvariant()), names[third.UserId]);
            Assert.Equal(($"{name.ToUpperInvariant()} 3", $"{name} 3".ToUpperInvariant()), names[later.UserId]);
            Assert.DoesNotContain(await db.Users.Select(u => u.DisplayName).ToListAsync(), n => n is not null && n != n.Trim());
        });
    }

    // --- Playlists ---

    [Fact]
    public async Task OthersPlaylists_AreNotFound_EvenWhenDeletingOrSharing()
    {
        var owner = await _factory.CreateUserAsync();
        var stranger = await _factory.CreateUserAsync();
        var playlist = await CreatePlaylistAsync(owner.Client);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.DeleteAsync($"/api/playlists/{playlist.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/shares", new SharePlaylistRequest(stranger.UserId, "Editor"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.DeleteAsync($"/api/playlists/{playlist.Id}/shares/{owner.UserId}")).StatusCode);
    }

    [Fact]
    public async Task SharedPlaylists_StillRefuseDeletionByNonOwners()
    {
        var owner = await _factory.CreateUserAsync();
        var friend = await _factory.CreateUserAsync();
        var playlist = await CreatePlaylistAsync(owner.Client);
        (await owner.Client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/shares", new SharePlaylistRequest(friend.UserId, "Editor"))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await friend.Client.DeleteAsync($"/api/playlists/{playlist.Id}")).StatusCode);
    }

    [Fact]
    public async Task PlaylistNames_HaveALengthLimit()
    {
        var client = await _factory.CreateUserClientAsync();

        var response = await client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(new string('x', 201)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

    private async Task<LoginResponse> SignInAsync(TestUser user)
    {
        var response = await LoginAsync(_factory.CreateClient(), user.Email, user.Password);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<PlaylistDto> CreatePlaylistAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Mine"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaylistDto>())!;
    }

    private HttpClient ClientWith(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>A server with a low sign-in rate limit.</summary>
public class RateLimitedApiFactory : FossegrimApiFactory
{
    public const int Limit = 3;

    public RateLimitedApiFactory()
    {
        SignInPerMinute = Limit;
    }
}

public class SignInRateLimitTests : IClassFixture<RateLimitedApiFactory>
{
    private readonly RateLimitedApiFactory _factory;

    public SignInRateLimitTests(RateLimitedApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SignInsBeyondTheLimit_AreRefusedForAMinute()
    {
        var client = _factory.CreateClient();
        var request = new LoginRequest($"nobody-{Guid.NewGuid():N}@example.com", "wrong");

        for (var i = 0; i < RateLimitedApiFactory.Limit; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
        }
        var limited = await client.PostAsJsonAsync("/api/auth/login", request);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
    }
}

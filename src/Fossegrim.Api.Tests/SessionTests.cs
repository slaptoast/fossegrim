using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Microsoft.IdentityModel.Tokens;

namespace Fossegrim.Api.Tests;

public class SessionTests : IClassFixture<FossegrimApiFactory>
{
    private const string Firefox = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.6; rv:132.0) Gecko/20100101 Firefox/132.0";

    private readonly FossegrimApiFactory _factory;

    public SessionTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_ReturnsARefreshToken_AndListsTheDevice()
    {
        var user = await _factory.CreateUserAsync();

        var (client, login) = await SignInAsync(user, "Fossegrim for iOS", Firefox);

        Assert.StartsWith("fsr_", login.RefreshToken);
        Assert.NotNull(login.SessionId);
        var sessions = await client.GetFromJsonAsync<List<SessionDto>>("/api/auth/sessions");
        var current = Assert.Single(sessions!, s => s.IsCurrent);
        Assert.Equal((login.SessionId, "Fossegrim for iOS", "Firefox on macOS"), ((Guid?)current.Id, current.ClientName, current.Device));
        // The factory's own sign-in is listed too, as another device.
        Assert.Equal(2, sessions!.Count);
    }

    [Fact]
    public async Task Refresh_ReplacesTheRefreshToken_AndIssuesAWorkingAccessToken()
    {
        var user = await _factory.CreateUserAsync();
        var (_, login) = await SignInAsync(user);

        var refreshed = await RefreshAsync(login.RefreshToken!);

        Assert.NotNull(refreshed);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(login.SessionId, refreshed.SessionId);
        Assert.Equal(HttpStatusCode.OK, (await ClientWith(refreshed.Token).GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ReplacedRefreshToken_KeepsWorking_UntilTheNewOneIsUsed()
    {
        var user = await _factory.CreateUserAsync();
        var (_, login) = await SignInAsync(user);
        var first = login.RefreshToken!;

        // The app never received the response with `second`, so it retries with `first`.
        var second = (await RefreshAsync(first))!.RefreshToken!;
        var third = (await RefreshAsync(first))!.RefreshToken!;

        Assert.Null(await RefreshAsync(second));
        var fourth = await RefreshAsync(third);
        Assert.NotNull(fourth);
        // `third` was used, so the app has moved on: `first` is finished.
        Assert.Null(await RefreshAsync(first));
    }

    [Fact]
    public async Task Refresh_WithAnUnknownToken_IsUnauthorized()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("fsr_not-a-real-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SigningADeviceOut_RejectsItsTokens_Immediately()
    {
        var user = await _factory.CreateUserAsync();
        var (phone, phoneLogin) = await SignInAsync(user, "Phone");

        var response = await user.Client.DeleteAsync($"/api/auth/sessions/{phoneLogin.SessionId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Null(await RefreshAsync(phoneLogin.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task OtherUsersSessions_CantBeSignedOut()
    {
        var owner = await _factory.CreateUserAsync();
        var (ownerClient, ownerLogin) = await SignInAsync(owner);
        var other = await _factory.CreateUserAsync();

        var response = await other.Client.DeleteAsync($"/api/auth/sessions/{ownerLogin.SessionId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsOnlyThisDevice()
    {
        var user = await _factory.CreateUserAsync();
        var (laptop, _) = await SignInAsync(user, "Laptop");

        var response = await laptop.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SignOutEverywhereElse_KeepsOnlyThisDevice()
    {
        var user = await _factory.CreateUserAsync();
        var (phone, _) = await SignInAsync(user, "Phone");
        var (tablet, _) = await SignInAsync(user, "Tablet");

        var response = await user.Client.DeleteAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/auth/me")).StatusCode);
        var sessions = await user.Client.GetFromJsonAsync<List<SessionDto>>("/api/auth/sessions");
        Assert.True(Assert.Single(sessions!).IsCurrent);
    }

    [Fact]
    public async Task ChangingThePassword_SignsOutOtherDevices()
    {
        var user = await _factory.CreateUserAsync();
        var (phone, _) = await SignInAsync(user, "Phone");

        var response = await user.Client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(user.Password, "N3w-Passw0rd!"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SignedOutTokens_AreRejected_InStreamAndCoverUrlsToo()
    {
        var user = await _factory.CreateUserAsync();
        var (device, login) = await SignInAsync(user);

        // Signed in, an unknown album's cover is a 404; signed out, a 401.
        var url = $"/cover/{Guid.NewGuid()}?access_token={Uri.EscapeDataString(login.MediaToken!)}";
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(url)).StatusCode);

        await device.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
    }

    // Every token carries a session id, so one without is rejected (it
    // could only come from before sessions existed, and those have expired).
    [Fact]
    public async Task TokensWithoutASession_AreRejected()
    {
        var user = await _factory.CreateUserAsync();
        var token = new JwtSecurityToken(
            issuer: "Fossegrim",
            audience: "FossegrimClients",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, user.UserId), new Claim(ClaimTypes.Name, user.Email)],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(FossegrimApiFactory.JwtKey)), SecurityAlgorithms.HmacSha256));

        var response = await ClientWith(new JwtSecurityTokenHandler().WriteToken(token)).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(Firefox, "Firefox on macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1", "Safari on iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("okhttp/4.12.0", null)]
    [InlineData(null, null)]
    public void UserAgents_AreDescribedForTheDevicesList(string? userAgent, string? expected)
    {
        Assert.Equal(expected, UserAgents.Describe(userAgent));
    }

    private async Task<(HttpClient Client, LoginResponse Login)> SignInAsync(TestUser user, string? clientName = null, string? userAgent = null)
    {
        var client = _factory.CreateClient();
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password, clientName));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return (client, login);
    }

    private async Task<LoginResponse?> RefreshAsync(string refreshToken)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<LoginResponse>() : null;
    }

    private HttpClient ClientWith(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

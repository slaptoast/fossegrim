using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Fossegrim.Api.Tests;

// The Blazor AppearanceService against the real preferences endpoints.
public class AppearanceServiceTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public AppearanceServiceTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Load_AppliesTheSavedTheme()
    {
        var user = await _factory.CreateUserAsync();
        await user.Client.PutAsync("/api/me/preferences", Json("""{"theme":{"choice":"dark","custom":[]}}"""));
        var (service, js) = Create(user);

        await service.EnsureLoadedAsync();

        Assert.True(service.IsLoaded);
        Assert.Equal(ThemeChoices.Dark, service.Current.Theme.Choice);
        Assert.Equal("dark", js.LastApplied!.Value.GetProperty("base").GetString());
    }

    [Fact]
    public async Task Update_AppliesAtOnce_AndSavesShortlyAfter()
    {
        var user = await _factory.CreateUserAsync();
        var (service, js) = Create(user);
        await service.EnsureLoadedAsync();
        var theme = new CustomTheme { Name = "Fjord", Base = "dark", Colors = { ["--canvas"] = "#102030" } };

        await service.UpdateAsync(p =>
        {
            p.Theme.Custom.Add(theme);
            p.Theme.Choice = ThemeChoices.ForCustom(theme);
        });

        var applied = js.LastApplied!.Value;
        Assert.Equal("dark", applied.GetProperty("base").GetString());
        Assert.Equal("#102030", applied.GetProperty("colors").GetProperty("--canvas").GetString());

        var saved = await WaitForSavedAsync(user, s => s.Contains("Fjord"));
        Assert.Equal(ThemeChoices.ForCustom(theme), AppearancePreferencesFile.Parse(saved)!.Theme.Choice);
    }

    [Fact]
    public async Task Replace_SavesStraightAway()
    {
        var user = await _factory.CreateUserAsync();
        var (service, _) = Create(user);
        await service.EnsureLoadedAsync();

        await service.ReplaceAsync(new AppearancePreferences { Player = new PlayerSettings { Position = "top" } });

        var saved = AppearancePreferencesFile.Parse(await user.Client.GetStringAsync("/api/me/preferences"))!;
        Assert.Equal("top", saved.Player.Position);
    }

    [Fact]
    public async Task WhenLoadingFails_ChangesAreNeverSaved()
    {
        var user = await _factory.CreateUserAsync();
        await user.Client.PutAsync("/api/me/preferences", Json("""{"theme":{"choice":"dark","custom":[]}}"""));
        var failingGets = _factory.CreateDefaultClient(new FailPreferencesLoadHandler());
        failingGets.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        var toasts = new List<string>();
        var (service, _) = Create(user, failingGets, toasts);

        await service.EnsureLoadedAsync();
        await service.UpdateAsync(p => p.Theme.Choice = ThemeChoices.Light);
        await service.ReplaceAsync(new AppearancePreferences());
        await Task.Delay(1000); // past the save delay

        Assert.False(service.IsLoaded);
        Assert.Contains(toasts, t => t.StartsWith("Couldn't load"));
        // The saved settings weren't overwritten with defaults.
        Assert.Equal(ThemeChoices.Dark, AppearancePreferencesFile.Parse(await user.Client.GetStringAsync("/api/me/preferences"))!.Theme.Choice);
    }

    [Fact]
    public async Task SignedOut_DoesNothing()
    {
        var user = await _factory.CreateUserAsync();
        var (service, js) = Create(user, signedIn: false);

        await service.EnsureLoadedAsync();

        Assert.False(service.IsLoaded);
        Assert.Null(js.LastApplied);
    }

    private (AppearanceService Service, RecordingJs Js) Create(TestUser user, HttpClient? client = null, List<string>? toasts = null, bool signedIn = true)
    {
        var js = new RecordingJs();
        var api = new FossegrimApiClient(client ?? user.Client, new TokenStore(js));
        var toastService = new ToastService();
        if (toasts is not null)
        {
            toastService.Shown += toasts.Add;
        }
        return (new AppearanceService(api, new FixedAuth(signedIn ? user.UserId : null), js, toastService), js);
    }

    private static async Task<string> WaitForSavedAsync(TestUser user, Func<string, bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            var saved = await user.Client.GetStringAsync("/api/me/preferences");
            if (condition(saved))
            {
                return saved;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The settings were never saved.");
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed class FixedAuth(string? userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(userId is null
                ? new ClaimsIdentity()
                : new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId)], "test"))));
    }

    // Records what the service asks js/theme.js to apply.
    private sealed class RecordingJs : IJSRuntime
    {
        public JsonElement? LastApplied { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "fossegrimTheme.apply")
            {
                LastApplied = JsonSerializer.SerializeToElement(args![0]);
            }
            return new(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FailPreferencesLoadHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/me/preferences"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, cancellationToken);
    }
}

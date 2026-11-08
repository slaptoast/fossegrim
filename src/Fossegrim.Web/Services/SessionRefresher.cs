using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

public enum RefreshOutcome
{
    // The store now holds a working access token.
    Refreshed,
    // The session is gone (signed out elsewhere, or there's no refresh token).
    SignedOut,
    // The Api couldn't be reached or failed; try again later.
    Failed,
}

/// <summary>
/// Swaps the stored refresh token for new tokens. Access tokens are short
/// lived and renewed shortly before they expire (a rejected one is renewed
/// straight away). The media token in stream and cover URLs lasts days and
/// is renewed a day before it expires, since those URLs can't be retried
/// after a 401; keeping it between refreshes keeps the URLs stable. Uses its
/// own HttpClient, without AuthorizedHttpMessageHandler, so refreshing never
/// recurses.
/// </summary>
public class SessionRefresher
{
    public const string HttpClientName = "session-refresh";

    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RenewMediaBefore = TimeSpan.FromDays(1);

    private readonly TokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SessionRefresher(TokenStore tokenStore, IHttpClientFactory httpClientFactory)
    {
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Renews the access token if it's about to expire; does nothing when signed out.</summary>
    public async Task EnsureFreshAsync()
    {
        if (await _tokenStore.GetTokenAsync() is { } token && ExpiresSoon(token))
        {
            await RefreshAsync(token);
        }
    }

    /// <summary>Renews the tokens if the media token is missing or about to expire; does nothing when signed out.</summary>
    public async Task EnsureMediaTokenFreshAsync()
    {
        if (await _tokenStore.GetTokenAsync() is { } token &&
            (await _tokenStore.GetMediaTokenAsync() is not { } mediaToken || ExpiresWithin(mediaToken, RenewMediaBefore)))
        {
            await RefreshAsync(token, media: true);
        }
    }

    /// <summary>
    /// Renews the tokens. <paramref name="staleToken"/> is the access token
    /// that was rejected or is expiring; if the store already holds a
    /// different, fresh one (another request or tab refreshed first), that's
    /// used instead.
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(string? staleToken) => await RefreshAsync(staleToken, media: false);

    // media: renewing for the media token, which is then replaced.
    private async Task<RefreshOutcome> RefreshAsync(string? staleToken, bool media)
    {
        await _lock.WaitAsync();
        try
        {
            if (await _tokenStore.ReloadTokenAsync() is { } current && current != staleToken && !ExpiresSoon(current) &&
                (!media || await _tokenStore.GetMediaTokenAsync() is { } mediaToken && !ExpiresWithin(mediaToken, RenewMediaBefore)))
            {
                return RefreshOutcome.Refreshed;
            }

            if (await _tokenStore.GetRefreshTokenAsync() is not { Length: > 0 } refreshToken)
            {
                return RefreshOutcome.SignedOut;
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClientFactory.CreateClient(HttpClientName)
                    .PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken));
            }
            catch (HttpRequestException)
            {
                return RefreshOutcome.Failed;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return RefreshOutcome.SignedOut;
                }
                if (!response.IsSuccessStatusCode || await response.Content.ReadFromJsonAsync<LoginResponse>() is not { } login)
                {
                    return RefreshOutcome.Failed;
                }

                var stored = await _tokenStore.GetMediaTokenAsync();
                await _tokenStore.SetSessionAsync(login, replaceMediaToken: media || stored is null || ExpiresWithin(stored, RenewMediaBefore));
                return RefreshOutcome.Refreshed;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static bool ExpiresSoon(string token) => ExpiresWithin(token, RenewBefore);

    private static bool ExpiresWithin(string token, TimeSpan window)
    {
        var handler = new JwtSecurityTokenHandler();
        return !handler.CanReadToken(token) || handler.ReadJwtToken(token).ValidTo - DateTime.UtcNow < window;
    }
}

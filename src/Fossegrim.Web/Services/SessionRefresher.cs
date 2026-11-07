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
/// Swaps the stored refresh token for new tokens. Access tokens are renewed
/// a day before they expire, so the stream and cover URLs built from them
/// (which can't be retried after a 401) keep working; a rejected token is
/// renewed straight away. Uses its own HttpClient, without
/// AuthorizedHttpMessageHandler, so refreshing never recurses.
/// </summary>
public class SessionRefresher
{
    public const string HttpClientName = "session-refresh";

    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(1);

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

    /// <summary>
    /// Renews the tokens. <paramref name="staleToken"/> is the access token
    /// that was rejected or is expiring; if the store already holds a
    /// different, fresh one (another request or tab refreshed first), that's
    /// used instead.
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(string? staleToken)
    {
        await _lock.WaitAsync();
        try
        {
            if (await _tokenStore.ReloadTokenAsync() is { } current && current != staleToken && !ExpiresSoon(current))
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

                await _tokenStore.SetSessionAsync(login);
                return RefreshOutcome.Refreshed;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static bool ExpiresSoon(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        return !handler.CanReadToken(token) || handler.ReadJwtToken(token).ValidTo - DateTime.UtcNow < RenewBefore;
    }
}

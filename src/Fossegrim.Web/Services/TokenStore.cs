using Fossegrim.Contracts.Dtos;
using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// Wraps the browser's localStorage to persist the access token (a JWT), the
/// media token (for /stream and /cover URLs) and the refresh token across
/// page loads. The access and media tokens are cached in memory so repeated
/// reads within a session don't all round-trip through JS interop; the
/// refresh token is always read from storage, because another tab may have
/// refreshed and replaced it.
/// </summary>
public class TokenStore
{
    private const string StorageKey = "fossegrim_token";
    private const string RefreshStorageKey = "fossegrim_refresh_token";
    private const string MediaStorageKey = "fossegrim_media_token";

    private readonly IJSRuntime _jsRuntime;
    private string? _cachedToken;
    private string? _cachedMediaToken;
    private bool _hasLoaded;

    public TokenStore(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<string?> GetTokenAsync()
    {
        if (_hasLoaded)
        {
            return _cachedToken;
        }

        return await ReloadTokenAsync();
    }

    public async Task<string?> GetMediaTokenAsync()
    {
        if (!_hasLoaded)
        {
            await ReloadTokenAsync();
        }
        return _cachedMediaToken;
    }

    /// <summary>Re-reads the access and media tokens from storage, in case another tab replaced them.</summary>
    public async Task<string?> ReloadTokenAsync()
    {
        _cachedToken = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        _cachedMediaToken = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", MediaStorageKey);
        _hasLoaded = true;
        return _cachedToken;
    }

    public ValueTask<string?> GetRefreshTokenAsync() =>
        _jsRuntime.InvokeAsync<string?>("localStorage.getItem", RefreshStorageKey);

    /// <summary>
    /// Stores the tokens from signing in or refreshing. The media token is
    /// only replaced when <paramref name="replaceMediaToken"/> says so, so
    /// stream and cover URLs (and the browser's cached covers) stay the same
    /// across the frequent access token refreshes.
    /// </summary>
    public async Task SetSessionAsync(LoginResponse response, bool replaceMediaToken = true)
    {
        _cachedToken = response.Token;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, response.Token);
        if (response.MediaToken is { } mediaToken && (replaceMediaToken || _cachedMediaToken is null))
        {
            _cachedMediaToken = mediaToken;
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", MediaStorageKey, mediaToken);
        }
        if (response.RefreshToken is { } refreshToken)
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", RefreshStorageKey, refreshToken);
        }
    }

    public async Task ClearTokenAsync()
    {
        _cachedToken = null;
        _cachedMediaToken = null;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", MediaStorageKey);
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", RefreshStorageKey);
    }
}

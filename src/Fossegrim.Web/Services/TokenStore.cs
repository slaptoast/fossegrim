using Fossegrim.Contracts.Dtos;
using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// Wraps the browser's localStorage to persist the access token (a JWT) and
/// refresh token across page loads. The access token is cached in memory so
/// repeated reads within a session don't all round-trip through JS interop;
/// the refresh token is always read from storage, because another tab may
/// have refreshed and replaced it.
/// </summary>
public class TokenStore
{
    private const string StorageKey = "fossegrim_token";
    private const string RefreshStorageKey = "fossegrim_refresh_token";

    private readonly IJSRuntime _jsRuntime;
    private string? _cachedToken;
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

    /// <summary>Re-reads the access token from storage, in case another tab replaced it.</summary>
    public async Task<string?> ReloadTokenAsync()
    {
        _cachedToken = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        _hasLoaded = true;
        return _cachedToken;
    }

    public ValueTask<string?> GetRefreshTokenAsync() =>
        _jsRuntime.InvokeAsync<string?>("localStorage.getItem", RefreshStorageKey);

    /// <summary>Stores the tokens from signing in or refreshing.</summary>
    public async Task SetSessionAsync(LoginResponse response)
    {
        _cachedToken = response.Token;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, response.Token);
        if (response.RefreshToken is { } refreshToken)
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", RefreshStorageKey, refreshToken);
        }
    }

    public async Task ClearTokenAsync()
    {
        _cachedToken = null;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", RefreshStorageKey);
    }
}

using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// Wraps the browser's localStorage to persist the JWT across page loads.
/// Caches the value in memory so repeated reads within a session don't all
/// round-trip through JS interop.
/// </summary>
public class TokenStore
{
    private const string StorageKey = "fossegrim_token";

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

        _cachedToken = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        _hasLoaded = true;
        return _cachedToken;
    }

    public async Task SetTokenAsync(string token)
    {
        _cachedToken = token;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, token);
    }

    public async Task ClearTokenAsync()
    {
        _cachedToken = null;
        _hasLoaded = true;
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", StorageKey);
    }
}

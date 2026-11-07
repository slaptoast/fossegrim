using System.Net;
using System.Net.Http.Headers;

namespace Fossegrim.Web.Services;

/// <summary>
/// Attaches the stored JWT as a bearer token on every request to Fossegrim.Api.
/// If the Api rejects it (401), the session is refreshed and the request
/// sent once more; the user is only signed out when the session itself has
/// ended (signed out from another device, or a password change).
/// </summary>
public class AuthorizedHttpMessageHandler : DelegatingHandler
{
    private readonly TokenStore _tokenStore;
    private readonly JwtAuthenticationStateProvider _authStateProvider;
    private readonly SessionRefresher _refresher;

    public AuthorizedHttpMessageHandler(TokenStore tokenStore, JwtAuthenticationStateProvider authStateProvider, SessionRefresher refresher)
    {
        _tokenStore = tokenStore;
        _authStateProvider = authStateProvider;
        _refresher = refresher;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _refresher.EnsureFreshAsync();
        var token = await _tokenStore.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Buffered so the body can be sent again after a refresh.
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var outcome = await _refresher.RefreshAsync(token);
        if (outcome == RefreshOutcome.Refreshed && await _tokenStore.GetTokenAsync() is { } fresh)
        {
            using var retry = await CloneAsync(request, cancellationToken);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            response.Dispose();
            response = await base.SendAsync(retry, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }
            outcome = RefreshOutcome.SignedOut;
        }

        if (outcome == RefreshOutcome.SignedOut)
        {
            await _tokenStore.ClearTokenAsync();
            _authStateProvider.MarkUserAsLoggedOut();
        }

        return response;
    }

    // An HttpRequestMessage can only be sent once.
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers.Where(h => h.Key != "Authorization"))
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (request.Content is not null)
        {
            clone.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clone;
    }
}

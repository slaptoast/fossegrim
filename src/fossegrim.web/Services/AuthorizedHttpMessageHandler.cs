using System.Net;
using System.Net.Http.Headers;

namespace Fossegrim.Web.Services;

/// <summary>
/// Attaches the stored JWT as a bearer token on every request to Fossegrim.Api,
/// and signs the user out if the Api ever responds 401 (expired/invalid token).
/// </summary>
public class AuthorizedHttpMessageHandler : DelegatingHandler
{
    private readonly TokenStore _tokenStore;
    private readonly JwtAuthenticationStateProvider _authStateProvider;

    public AuthorizedHttpMessageHandler(TokenStore tokenStore, JwtAuthenticationStateProvider authStateProvider)
    {
        _tokenStore = tokenStore;
        _authStateProvider = authStateProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenStore.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _tokenStore.ClearTokenAsync();
            _authStateProvider.MarkUserAsLoggedOut();
        }

        return response;
    }
}

using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

/// <summary>The web app's FossegrimApiClient, talking to the test Api through <c>client</c>.</summary>
internal static class WebClients
{
    public static FossegrimApiClient Create(HttpClient client, TokenStore tokens) =>
        new(client, tokens, new SessionRefresher(tokens, new SingleClientFactory(client)));

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

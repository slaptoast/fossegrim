using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Reads a public image's tags from the GitHub Container Registry (ghcr.io)
/// with an anonymous pull token. Used by the update check to find the latest
/// published release.
/// </summary>
public class GhcrClient
{
    public const string DefaultRepository = "slaptoast/fossegrim";

    private static readonly Uri Registry = new("https://ghcr.io/");

    // Each release pushes a handful of tags (1.2.3, 1.2, 1, latest, sha-...);
    // the cap only guards against an endless chain of "next" links.
    private const int PageSize = 1000;
    private const int MaxPages = 10;

    private readonly HttpClient _http;

    public GhcrClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Every tag of the image, or none if it doesn't exist or isn't public.</summary>
    public async Task<IReadOnlyList<string>> GetTagNamesAsync(string repository, CancellationToken cancellationToken = default)
    {
        // Even public images need a token, which ghcr.io hands out to anyone.
        // It refuses one for an image that doesn't exist or is private, which
        // to us means there are no releases to see.
        using var tokenResponse = await _http.GetAsync(new Uri(Registry, $"token?scope=repository:{repository}:pull"), cancellationToken);
        if (tokenResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            return [];
        }
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))?.Token
            ?? throw new HttpRequestException("ghcr.io returned no token.");

        var tags = new List<string>();
        Uri? url = new(Registry, $"v2/{repository}/tags/list?n={PageSize}");
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return tags;
            }
            response.EnsureSuccessStatusCode();
            var list = await response.Content.ReadFromJsonAsync<TagList>(cancellationToken)
                ?? throw new HttpRequestException("ghcr.io returned an empty response.");

            tags.AddRange(list.Tags?.Where(t => !string.IsNullOrEmpty(t)) ?? []);
            url = NextPage(response);
        }

        return tags;
    }

    // The registry pages with a Link header: </v2/...?last=x&n=y>; rel="next".
    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }
        foreach (var link in links)
        {
            var start = link.IndexOf('<');
            var end = link.IndexOf('>');
            if (start >= 0 && end > start && link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(Registry, link[(start + 1)..end]);
            }
        }
        return null;
    }

    private record TokenResponse(string? Token);

    private record TagList(List<string>? Tags);
}

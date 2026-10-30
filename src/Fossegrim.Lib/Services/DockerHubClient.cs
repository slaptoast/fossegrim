using System.Net.Http.Json;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Reads an image's tags from Docker Hub's public (anonymous) API, newest
/// first. Used by the update check to find the latest published release.
/// </summary>
public class DockerHubClient
{
    public const string DefaultRepository = "slaptoast/fossegrim";

    // Each release pushes a handful of tags (1.2.3, 1.2, 1, latest, sha-...),
    // so the first page almost always has the newest release; the cap only
    // guards against an endless "next" chain.
    private const int MaxPages = 5;

    private readonly HttpClient _http;

    public DockerHubClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<string>> GetTagNamesAsync(string repository, CancellationToken cancellationToken = default)
    {
        var tags = new List<string>();
        string? url = $"https://hub.docker.com/v2/repositories/{repository}/tags?page_size=100&ordering=last_updated";

        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            var response = await _http.GetFromJsonAsync<TagPage>(url, cancellationToken)
                ?? throw new HttpRequestException("Docker Hub returned an empty response.");

            tags.AddRange(response.Results?.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)) ?? []);
            url = response.Next;
        }

        return tags;
    }

    private record TagPage(string? Next, List<Tag>? Results);

    private record Tag(string Name);
}

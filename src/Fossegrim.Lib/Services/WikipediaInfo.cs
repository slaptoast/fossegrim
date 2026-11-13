using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Fossegrim.Lib.Services;

public enum InfoLookupOutcome
{
    Found,
    // The services answered, but have nothing for it: worth remembering.
    NotFound,
    // Couldn't ask (offline, rate limited, an error): try again next time.
    Unavailable,
}

public sealed record InfoLookup(InfoLookupOutcome Outcome, string? Summary = null, string? Url = null, string? Country = null)
{
    public static readonly InfoLookup NotFound = new(InfoLookupOutcome.NotFound);
    public static readonly InfoLookup Unavailable = new(InfoLookupOutcome.Unavailable);
}

/// <summary>
/// Finds the English Wikipedia summary for an artist or album, for the hover
/// cards: MusicBrainz finds it by name and links its Wikidata item, Wikidata
/// gives the Wikipedia page, and Wikipedia's summary API the first
/// paragraph. None of them needs a key. MusicBrainz shares its one request
/// a second with the cover search (<see cref="CoverSearchLimits"/>).
/// </summary>
public class WikipediaInfoService
{
    // Two MusicBrainz requests per lookup: the second usually waits a second.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultMusicBrainzBlock = TimeSpan.FromSeconds(10);
    // MusicBrainz scores matches 0-100; below this it's likely something else.
    private const int MinScore = 90;

    private readonly HttpClient _http;
    private readonly CoverSearchLimits _limits;

    public WikipediaInfoService(HttpClient http, CoverSearchLimits limits)
    {
        _http = http;
        _limits = limits;
    }

    public async Task<InfoLookup> ArtistAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return InfoLookup.NotFound;
        }
        var query = $"artist:\"{CoverSearchService.EscapeLucene(name.Trim())}\"";
        var search = await MusicBrainzAsync($"artist?query={Uri.EscapeDataString(query)}&fmt=json&limit=5", cancellationToken);
        if (search is null)
        {
            return InfoLookup.Unavailable;
        }
        using (search)
        {
            var match = BestMatch(search.RootElement, "artists", name);
            if (match is not { } artist)
            {
                return InfoLookup.NotFound;
            }
            var country = artist.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var result = await FromMusicBrainzEntityAsync("artist", artist.GetProperty("id").GetString()!, cancellationToken);
            return result.Outcome == InfoLookupOutcome.Unavailable ? result : result with { Country = country };
        }
    }

    public async Task<InfoLookup> AlbumAsync(string name, string? artist, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return InfoLookup.NotFound;
        }
        var query = $"releasegroup:\"{CoverSearchService.EscapeLucene(name.Trim())}\"";
        if (!string.IsNullOrWhiteSpace(artist))
        {
            query += $" AND artist:\"{CoverSearchService.EscapeLucene(artist.Trim())}\"";
        }
        var search = await MusicBrainzAsync($"release-group?query={Uri.EscapeDataString(query)}&fmt=json&limit=5", cancellationToken);
        if (search is null)
        {
            return InfoLookup.Unavailable;
        }
        using (search)
        {
            return BestMatch(search.RootElement, "release-groups", name, "title") is { } group
                ? await FromMusicBrainzEntityAsync("release-group", group.GetProperty("id").GetString()!, cancellationToken)
                : InfoLookup.NotFound;
        }
    }

    // The first result scored well enough whose name matches, ignoring case and accents.
    private static JsonElement? BestMatch(JsonElement root, string list, string name, string nameProperty = "name")
    {
        if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var wanted = Fold(name);
        foreach (var item in items.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            if (score >= MinScore && item.TryGetProperty(nameProperty, out var n) && Fold(n.GetString()) == wanted)
            {
                return item;
            }
        }
        return null;
    }

    // An artist or release group's links: its Wikidata item (or, on older
    // entries, a Wikipedia page straight away).
    private async Task<InfoLookup> FromMusicBrainzEntityAsync(string kind, string id, CancellationToken cancellationToken)
    {
        var entity = await MusicBrainzAsync($"{kind}/{Uri.EscapeDataString(id)}?inc=url-rels&fmt=json", cancellationToken);
        if (entity is null)
        {
            return InfoLookup.Unavailable;
        }

        string? wikidata = null;
        string? wikipedia = null;
        using (entity)
        {
            if (entity.RootElement.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
            {
                foreach (var relation in relations.EnumerateArray())
                {
                    var type = relation.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var url = relation.TryGetProperty("url", out var u) && u.TryGetProperty("resource", out var r) ? r.GetString() : null;
                    if (url is null)
                    {
                        continue;
                    }
                    if (type == "wikidata")
                    {
                        wikidata ??= url;
                    }
                    else if (type == "wikipedia" && url.Contains("://en.wikipedia.org/wiki/", StringComparison.OrdinalIgnoreCase))
                    {
                        wikipedia ??= url;
                    }
                }
            }
        }

        string? title = wikipedia is not null ? Uri.UnescapeDataString(wikipedia[(wikipedia.IndexOf("/wiki/", StringComparison.Ordinal) + "/wiki/".Length)..]) : null;
        if (title is null && wikidata is not null)
        {
            var (found, englishTitle) = await EnglishWikipediaTitleAsync(wikidata, cancellationToken);
            if (!found)
            {
                return InfoLookup.Unavailable;
            }
            title = englishTitle;
        }
        return title is null ? InfoLookup.NotFound : await WikipediaSummaryAsync(title, cancellationToken);
    }

    // found: Wikidata answered (the title is null when there's no English article).
    private async Task<(bool Found, string? Title)> EnglishWikipediaTitleAsync(string wikidataUrl, CancellationToken cancellationToken)
    {
        var item = wikidataUrl.TrimEnd('/')[(wikidataUrl.TrimEnd('/').LastIndexOf('/') + 1)..];
        if (!item.StartsWith('Q') || !item[1..].All(char.IsAsciiDigit))
        {
            return (true, null);
        }
        using var json = await GetJsonAsync($"https://www.wikidata.org/wiki/Special:EntityData/{item}.json", cancellationToken);
        if (json is null)
        {
            return (false, null);
        }
        return json.RootElement.TryGetProperty("entities", out var entities)
               && entities.EnumerateObject().FirstOrDefault().Value is { ValueKind: JsonValueKind.Object } entity
               && entity.TryGetProperty("sitelinks", out var links)
               && links.TryGetProperty("enwiki", out var enwiki)
               && enwiki.TryGetProperty("title", out var title)
            ? (true, title.GetString())
            : (true, null);
    }

    private async Task<InfoLookup> WikipediaSummaryAsync(string title, CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(title.Replace(' ', '_'));
        using var json = await GetJsonAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{path}", cancellationToken);
        if (json is null)
        {
            return InfoLookup.Unavailable;
        }
        var root = json.RootElement;
        // A disambiguation page isn't about this artist or album.
        if (root.TryGetProperty("type", out var type) && type.GetString() == "disambiguation")
        {
            return InfoLookup.NotFound;
        }
        var extract = root.TryGetProperty("extract", out var e) ? e.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(extract))
        {
            return InfoLookup.NotFound;
        }
        var page = root.TryGetProperty("content_urls", out var urls) && urls.TryGetProperty("desktop", out var desktop)
                   && desktop.TryGetProperty("page", out var p) ? p.GetString() : null;
        return new InfoLookup(InfoLookupOutcome.Found, extract, page ?? $"https://en.wikipedia.org/wiki/{path}");
    }

    // A MusicBrainz request within its rate limit; null when it couldn't be made.
    private async Task<JsonDocument?> MusicBrainzAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var limit = _limits.MusicBrainz;
        if (await limit.AcquireAsync(MaxWait, cancellationToken) is not null)
        {
            return null;
        }
        using var request = Request($"https://musicbrainz.org/ws/2/{pathAndQuery}");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                limit.Block(response.Headers.RetryAfter?.Delta ?? DefaultMusicBrainzBlock,
                    $"MusicBrainz is limiting requests (HTTP {(int)response.StatusCode}). It allows about one request per second for this whole server.");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                limit.MarkFailed($"MusicBrainz answered with an error (HTTP {(int)response.StatusCode}).");
                return null;
            }
            var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            limit.MarkSucceeded();
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            limit.MarkFailed($"Couldn't reach MusicBrainz: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = Request(url);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // MusicBrainz and Wikimedia both ask clients to say who they are.
        request.Headers.UserAgent.ParseAdd(CoverSearchService.UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static string Fold(string? text)
    {
        var decomposed = (text ?? "").Trim().Normalize(NormalizationForm.FormD);
        var kept = decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
        return new string(kept.ToArray()).Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}

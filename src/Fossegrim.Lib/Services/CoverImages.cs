using System.Net;

namespace Fossegrim.Lib.Services;

/// <summary>Checks on cover images, whether uploaded or downloaded.</summary>
public static class CoverImages
{
    // Covers are written into every track of an album, so keep them modest.
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>"image/jpeg" or "image/png" from the file's first bytes; null for anything else.</summary>
    public static string? DetectMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        return null;
    }

    /// <summary>
    /// Whether the server may download from <paramref name="url"/>: HTTPS on
    /// the search providers' image hosts only. The address comes from the
    /// browser, so without this the server could be pointed at anything it can
    /// reach, including services on its own network.
    /// </summary>
    public static bool IsAllowedSource(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo))
        {
            return false;
        }

        var host = url.IdnHost.ToLowerInvariant();
        return host is "coverartarchive.org" or "archive.org"
            || host.EndsWith(".archive.org", StringComparison.Ordinal)   // where the Cover Art Archive's files are served from
            || host.EndsWith(".dzcdn.net", StringComparison.Ordinal);    // Deezer's image CDN
    }
}

public record CoverDownload(byte[]? Data, string? MimeType, string? Error);

/// <summary>
/// Downloads a chosen search result. Redirects are followed by hand so every
/// hop is checked against <see cref="CoverImages.IsAllowedSource"/>; register
/// its HttpClient with automatic redirects off.
/// </summary>
public class CoverDownloader
{
    private const int MaxRedirects = 5;
    private readonly HttpClient _http;

    public CoverDownloader(HttpClient http)
    {
        _http = http;
    }

    public async Task<CoverDownload> DownloadAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var url) || !CoverImages.IsAllowedSource(url))
        {
            return new(null, null, "That image isn't from a cover search provider.");
        }

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(CoverSearchService.UserAgent);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (!CoverImages.IsAllowedSource(url))
                    {
                        return new(null, null, "The image redirected somewhere other than a cover search provider.");
                    }
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new(null, null, response.StatusCode == HttpStatusCode.NotFound
                        ? "The provider no longer has that image."
                        : $"The provider answered with an error (HTTP {(int)response.StatusCode}).");
                }

                if (response.Content.Headers.ContentLength > CoverImages.MaxBytes)
                {
                    return new(null, null, "The image is larger than 10 MB.");
                }

                var data = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
                if (data is null)
                {
                    return new(null, null, "The image is larger than 10 MB.");
                }

                var mimeType = CoverImages.DetectMimeType(data);
                return mimeType is null
                    ? new(null, null, "The download wasn't a JPEG or PNG image.")
                    : new(data, mimeType, null);
            }

            return new(null, null, "The image redirected too many times.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new(null, null, $"Couldn't download the image: {(ex is TaskCanceledException ? "the provider didn't answer in time." : ex.Message)}");
        }
    }

    // Null when the stream holds more than MaxBytes.
    public static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > CoverImages.MaxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

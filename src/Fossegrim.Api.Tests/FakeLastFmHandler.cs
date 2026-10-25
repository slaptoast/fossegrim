using System.Net;
using System.Text;
using System.Text.Json;

namespace Fossegrim.Api.Tests;

/// <summary>
/// A scripted Last.fm API: records each request's form fields and answers
/// with <see cref="Respond"/> (an accepted scrobble unless a test changes it).
/// </summary>
public class FakeLastFmHandler : HttpMessageHandler
{
    public const string AcceptedScrobble =
        """{"scrobbles":{"@attr":{"accepted":1,"ignored":0},"scrobble":{"ignoredMessage":{"code":"0","#text":""}}}}""";

    public static string IgnoredScrobble(int code, string message) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["scrobbles"] = new Dictionary<string, object>
        {
            ["@attr"] = new { accepted = 0, ignored = 1 },
            ["scrobble"] = new { ignoredMessage = new Dictionary<string, string> { ["code"] = code.ToString(), ["#text"] = message } }
        }
    });

    public static string Error(int code, string message) => JsonSerializer.Serialize(new { error = code, message });

    public List<Dictionary<string, string>> Requests { get; } = [];

    public Func<Dictionary<string, string>, (HttpStatusCode Status, string Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, AcceptedScrobble);

    public IEnumerable<Dictionary<string, string>> Scrobbles => Requests.Where(r => r["method"] == "track.scrobble");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => WebUtility.UrlDecode(kv[0]), kv => WebUtility.UrlDecode(kv.Length > 1 ? kv[1] : ""));

        lock (Requests)
        {
            Requests.Add(form);
        }

        var (status, responseBody) = Respond(form);
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
    }
}

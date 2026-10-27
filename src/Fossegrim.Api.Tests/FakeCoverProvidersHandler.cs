using System.Net;
using System.Text;

namespace Fossegrim.Api.Tests;

/// <summary>
/// Stands in for MusicBrainz, the Cover Art Archive, Deezer and their image
/// hosts: records every request and answers with <see cref="Respond"/>
/// (404 for everything unless a test sets it).
/// </summary>
public class FakeCoverProvidersHandler : HttpMessageHandler
{
    private readonly object _lock = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public IEnumerable<HttpRequestMessage> RequestsTo(string host)
    {
        lock (_lock)
        {
            return Requests.Where(r => r.RequestUri!.Host == host).ToList();
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Image(byte[] data) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Requests.Add(request);
        }
        return Task.FromResult(Respond(request));
    }
}

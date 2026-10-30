using System.Net;
using System.Text;
using System.Text.Json;

namespace Fossegrim.Api.Tests;

/// <summary>
/// A scripted Docker Hub tags API: serves <see cref="Pages"/> in order,
/// linking each to the next, or fails with <see cref="FailWith"/> when set.
/// </summary>
public class FakeDockerHubHandler : HttpMessageHandler
{
    public List<string[]> Pages { get; set; } = [];

    public HttpStatusCode? FailWith { get; set; }

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        if (FailWith is { } status)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        var page = request.RequestUri!.Query.Contains("page=") ? int.Parse(request.RequestUri.Query.Split("page=")[1].Split('&')[0]) : 1;
        var next = page < Pages.Count ? $"https://hub.docker.com/v2/repositories/slaptoast/fossegrim/tags?page={page + 1}" : null;
        var body = JsonSerializer.Serialize(new
        {
            next,
            results = (page <= Pages.Count ? Pages[page - 1] : []).Select(name => new { name })
        });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

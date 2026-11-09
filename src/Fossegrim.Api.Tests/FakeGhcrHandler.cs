using System.Net;
using System.Text;
using System.Text.Json;

namespace Fossegrim.Api.Tests;

/// <summary>
/// A scripted ghcr.io: hands out a pull token, then serves <see cref="Pages"/>
/// of tags in order, linking each to the next with a Link header. With
/// <see cref="FailWith"/> set, the tag list fails with that status; with
/// <see cref="RefuseToken"/>, the token request is refused, as for an image
/// that doesn't exist or isn't public.
/// </summary>
public class FakeGhcrHandler : HttpMessageHandler
{
    private const string Token = "anonymous-pull-token";

    public List<string[]> Pages { get; set; } = [];

    public HttpStatusCode? FailWith { get; set; }

    public bool RefuseToken { get; set; }

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/token")
        {
            return Task.FromResult(RefuseToken
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json(new { token = Token }));
        }

        if (request.Headers.Authorization?.Parameter != Token)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
        if (FailWith is { } status)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        // Pages are numbered by "last", the way the registry continues a list.
        var page = uri.Query.Contains("last=page") ? int.Parse(uri.Query.Split("last=page")[1].Split('&')[0]) + 1 : 1;
        var response = Json(new
        {
            name = "slaptoast/fossegrim",
            tags = page <= Pages.Count ? Pages[page - 1] : []
        });
        if (page < Pages.Count)
        {
            response.Headers.Add("Link", $"</v2/slaptoast/fossegrim/tags/list?last=page{page}&n=1000>; rel=\"next\"");
        }
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

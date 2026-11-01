using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Fossegrim.Api.Tests;

// Canned answers from the cover services, shared by the tests below.
internal static class CoverFixtures
{
    public const string ReleaseGroups = """
        {"release-groups":[
          {"id":"rg-with-art","title":"Mezzanine","first-release-date":"1998-04-20","artist-credit":[{"name":"Massive Attack","joinphrase":""}]},
          {"id":"rg-without-art","title":"Mezzanine (Remastered)","artist-credit":[{"name":"Massive Attack"}]}
        ]}
        """;

    public const string ReleaseGroupArt = """
        {"images":[
          {"front":false,"image":"http://coverartarchive.org/release/r1/back.jpg","thumbnails":{}},
          {"front":true,"image":"http://coverartarchive.org/release/r1/front.jpg",
           "thumbnails":{"250":"http://coverartarchive.org/release/r1/front-250.jpg","1200":"http://coverartarchive.org/release/r1/front-1200.jpg"}}
        ]}
        """;

    public const string DeezerAlbums = """
        {"data":[{"title":"Mezzanine","artist":{"name":"Massive Attack"},
          "cover_medium":"https://e-cdns-images.dzcdn.net/images/cover/abc/250x250.jpg",
          "cover_xl":"https://e-cdns-images.dzcdn.net/images/cover/abc/1000x1000.jpg"}]}
        """;

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7];

    // Both providers answering normally.
    public static HttpResponseMessage Providers(HttpRequestMessage request) => request.RequestUri!.Host switch
    {
        "musicbrainz.org" => FakeCoverProvidersHandler.Json(ReleaseGroups),
        "coverartarchive.org" when request.RequestUri.AbsolutePath == "/release-group/rg-with-art" => FakeCoverProvidersHandler.Json(ReleaseGroupArt),
        "api.deezer.com" => FakeCoverProvidersHandler.Json(DeezerAlbums),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };
}

public class CoverSearchServiceTests
{
    private readonly FakeCoverProvidersHandler _http = new() { Respond = CoverFixtures.Providers };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-27T12:00:00Z"));
    private readonly CoverSearchLimits _limits;
    private readonly CoverSearchService _search;

    public CoverSearchServiceTests()
    {
        _limits = new CoverSearchLimits(_time);
        _search = new CoverSearchService(new HttpClient(_http), _limits);
    }

    [Fact]
    public async Task Search_CombinesTheArchivesArtAndDeezer()
    {
        var response = await _search.SearchAsync("Mezzanine", "Massive Attack");

        Assert.Collection(response.Results,
            mb =>
            {
                Assert.Equal((CoverSearchService.MusicBrainzId, "Mezzanine", "Massive Attack", "1998"), (mb.Provider, mb.Title, mb.Artist, mb.Year));
                // The front image, at 1200px, over https.
                Assert.Equal("https://coverartarchive.org/release/r1/front-1200.jpg", mb.ImageUrl);
                Assert.Equal("https://coverartarchive.org/release/r1/front-250.jpg", mb.PreviewUrl);
            },
            dz => Assert.Equal((CoverSearchService.DeezerId, "https://e-cdns-images.dzcdn.net/images/cover/abc/1000x1000.jpg"), (dz.Provider, dz.ImageUrl)));
        Assert.All(response.Providers, p => Assert.Equal("ready", p.State));

        var query = Uri.UnescapeDataString(Assert.Single(_http.RequestsTo("musicbrainz.org")).RequestUri!.Query);
        Assert.Contains("releasegroup:\"Mezzanine\" AND artist:\"Massive Attack\"", query);
        Assert.Contains("Fossegrim/", Assert.Single(_http.RequestsTo("musicbrainz.org")).Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task MusicBrainz_SecondSearchWithinASecond_WaitsForItsTurn()
    {
        await _search.SearchAsync("Mezzanine", null);

        var second = _search.SearchAsync("Mezzanine", null);
        Assert.False(second.IsCompleted); // waiting for MusicBrainz's next slot
        _time.Advance(TimeSpan.FromSeconds(1));
        var response = await second;

        Assert.Equal(2, _http.RequestsTo("musicbrainz.org").Count());
        Assert.Contains(response.Results, r => r.Provider == CoverSearchService.MusicBrainzId);
    }

    [Fact]
    public async Task MusicBrainzRefusing_IsLimitedUntilRetryAfter_AndNothingIsSentMeanwhile()
    {
        _http.Respond = request => request.RequestUri!.Host == "musicbrainz.org"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } }
            : CoverFixtures.Providers(request);

        var refused = await _search.SearchAsync("Mezzanine", null);

        var status = refused.Providers.Single(p => p.Provider == CoverSearchService.MusicBrainzId);
        Assert.Equal(("limited", 30), (status.State, status.RetryAfterSeconds));
        Assert.Contains("refused", status.Message);
        Assert.Contains(refused.Results, r => r.Provider == CoverSearchService.DeezerId); // the other still searched

        _http.Respond = CoverFixtures.Providers;
        _time.Advance(TimeSpan.FromSeconds(10));
        var meanwhile = await _search.SearchAsync("Mezzanine", null);
        Assert.Single(_http.RequestsTo("musicbrainz.org"));
        Assert.Equal(20, meanwhile.Providers.Single(p => p.Provider == CoverSearchService.MusicBrainzId).RetryAfterSeconds);

        _time.Advance(TimeSpan.FromSeconds(21));
        var after = await _search.SearchAsync("Mezzanine", null);
        Assert.Equal(2, _http.RequestsTo("musicbrainz.org").Count());
        Assert.Equal("ready", after.Providers.Single(p => p.Provider == CoverSearchService.MusicBrainzId).State);
    }

    [Fact]
    public async Task DeezerQuotaError_IsLimited()
    {
        _http.Respond = request => request.RequestUri!.Host == "api.deezer.com"
            ? FakeCoverProvidersHandler.Json("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""")
            : CoverFixtures.Providers(request);

        var response = await _search.SearchAsync("Mezzanine", null);

        var status = response.Providers.Single(p => p.Provider == CoverSearchService.DeezerId);
        Assert.Equal(("limited", 5), (status.State, status.RetryAfterSeconds));
        Assert.Contains("quota", status.Message);
    }

    [Fact]
    public async Task Unreachable_IsUnavailableWithTheReason_UntilASearchWorks()
    {
        _http.Respond = request => request.RequestUri!.Host == "api.deezer.com"
            ? throw new HttpRequestException("Name or service not known")
            : CoverFixtures.Providers(request);

        var failed = await _search.SearchAsync("Mezzanine", null);
        var status = failed.Providers.Single(p => p.Provider == CoverSearchService.DeezerId);
        Assert.Equal("unavailable", status.State);
        Assert.Equal("Couldn't reach Deezer: Name or service not known", status.Message);

        _http.Respond = CoverFixtures.Providers;
        _time.Advance(TimeSpan.FromSeconds(1));
        var recovered = await _search.SearchAsync("Mezzanine", null);
        Assert.Equal("ready", recovered.Providers.Single(p => p.Provider == CoverSearchService.DeezerId).State);
    }

    [Fact]
    public async Task OwnLimitUsedUp_IsLimited_AndTheProviderIsntAsked()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(await _limits.Deezer.AcquireAsync(TimeSpan.Zero, CancellationToken.None));
        }

        var response = await _search.SearchAsync("Mezzanine", null);

        var status = response.Providers.Single(p => p.Provider == CoverSearchService.DeezerId);
        Assert.Equal(("limited", 5, 0), (status.State, status.RetryAfterSeconds, status.RemainingRequests));
        Assert.StartsWith("Searching too quickly", status.Message);
        Assert.Empty(_http.RequestsTo("api.deezer.com"));
    }
}

public class CoverDownloaderTests
{
    private readonly FakeCoverProvidersHandler _http = new();
    private readonly CoverDownloader _downloader;

    public CoverDownloaderTests()
    {
        _downloader = new CoverDownloader(new HttpClient(_http));
    }

    [Theory]
    [InlineData("https://coverartarchive.org/release/r1/front-1200.jpg", true)]
    [InlineData("https://ia800102.us.archive.org/1/items/mbid-x/front.jpg", true)]
    [InlineData("https://e-cdns-images.dzcdn.net/images/cover/abc/1000x1000.jpg", true)]
    [InlineData("http://coverartarchive.org/release/r1/front.jpg", false)]   // not https
    [InlineData("https://coverartarchive.org:8443/release/r1/front.jpg", false)]
    [InlineData("https://user@coverartarchive.org/release/r1/front.jpg", false)]
    [InlineData("https://example.com/cover.jpg", false)]
    [InlineData("https://dzcdn.net.example.com/cover.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    public void OnlyProviderImageHosts_AreAllowed(string url, bool allowed)
    {
        Assert.Equal(allowed, CoverImages.IsAllowedSource(new Uri(url)));
    }

    [Fact]
    public async Task OtherHosts_AreNeverRequested()
    {
        var result = await _downloader.DownloadAsync("https://example.com/cover.jpg");

        Assert.Null(result.Data);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task RedirectsAreFollowed_OnlyToProviderHosts()
    {
        _http.Respond = request => request.RequestUri!.Host switch
        {
            "coverartarchive.org" => FakeCoverProvidersHandler.Redirect("https://ia800102.us.archive.org/front.jpg"),
            "ia800102.us.archive.org" => FakeCoverProvidersHandler.Image(CoverFixtures.Jpeg),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var followed = await _downloader.DownloadAsync("https://coverartarchive.org/release/r1/front-1200.jpg");
        Assert.Equal(CoverFixtures.Jpeg, followed.Data);
        Assert.Equal("image/jpeg", followed.MimeType);

        _http.Requests.Clear();
        _http.Respond = _ => FakeCoverProvidersHandler.Redirect("http://169.254.169.254/latest/meta-data");
        var blocked = await _downloader.DownloadAsync("https://coverartarchive.org/release/r1/front-1200.jpg");
        Assert.Null(blocked.Data);
        Assert.Contains("redirected", blocked.Error);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task NonImagesAndHugeFiles_AreRejected()
    {
        _http.Respond = _ => FakeCoverProvidersHandler.Json("""{"not":"an image"}""");
        Assert.Contains("JPEG or PNG", (await _downloader.DownloadAsync("https://coverartarchive.org/x.jpg")).Error);

        _http.Respond = _ => FakeCoverProvidersHandler.Image(new byte[CoverImages.MaxBytes + 1]);
        Assert.Contains("10 MB", (await _downloader.DownloadAsync("https://coverartarchive.org/x.jpg")).Error);
    }
}

public sealed class AlbumCoverEndpointTests : IClassFixture<FossegrimApiFactory>, IDisposable
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _musicFolder = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "covers-" + Guid.NewGuid().ToString("N"));
    private readonly string _album = "Cover Test " + Guid.NewGuid().ToString("N")[..8];

    public AlbumCoverEndpointTests(FossegrimApiFactory factory)
    {
        _factory = factory;
        _factory.Covers.Respond = CoverFixtures.Providers;
    }

    public void Dispose()
    {
        if (Directory.Exists(_musicFolder))
        {
            Directory.Delete(_musicFolder, recursive: true);
        }
    }

    [Fact]
    public async Task Upload_WritesTheFrontCoverIntoEveryTrack_AndKeepsOtherPictures()
    {
        var (albumId, first, second) = await CreateAlbumAsync(firstHasBackCover: true);
        var admin = await _factory.CreateAdminAsync();

        var response = await admin.Client.PutAsync($"/api/covers/albums/{albumId}", new ByteArrayContent(CoverFixtures.Png));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SetAlbumCoverResponse>();
        Assert.Equal(2, result!.Updated);
        Assert.Empty(result.Errors);

        Assert.Equal([(TagLib.PictureType.FrontCover, "image/png"), (TagLib.PictureType.BackCover, "image/jpeg")], Pictures(first).Select(p => (p.Type, p.MimeType)));
        Assert.Equal(CoverFixtures.Png, Pictures(first)[0].Data.Data);
        Assert.Equal(CoverFixtures.Png, Assert.Single(Pictures(second)).Data.Data);

        // And it's what the library now serves for the album.
        var cover = await admin.Client.GetAsync($"/cover/{albumId}");
        Assert.Equal("image/png", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal(CoverFixtures.Png, await cover.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FromSearch_DownloadsAndSavesTheResult()
    {
        var (albumId, first, _) = await CreateAlbumAsync();
        var admin = await _factory.CreateAdminAsync();
        _factory.Covers.Respond = request => request.RequestUri!.AbsolutePath == "/release/r1/front-1200.jpg"
            ? FakeCoverProvidersHandler.Image(CoverFixtures.Jpeg)
            : CoverFixtures.Providers(request);

        var response = await admin.Client.PostAsJsonAsync($"/api/covers/albums/{albumId}/from-search",
            new SetCoverFromSearchRequest("https://coverartarchive.org/release/r1/front-1200.jpg"));

        response.EnsureSuccessStatusCode();
        Assert.Equal(CoverFixtures.Jpeg, Assert.Single(Pictures(first)).Data.Data);
    }

    [Fact]
    public async Task FromSearch_RefusesOtherHosts_WithoutRequestingThem()
    {
        var (albumId, _, _) = await CreateAlbumAsync();
        var admin = await _factory.CreateAdminAsync();

        var response = await admin.Client.PostAsJsonAsync($"/api/covers/albums/{albumId}/from-search",
            new SetCoverFromSearchRequest("https://internal.example/secret.jpg"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Covers.RequestsTo("internal.example"));
    }

    [Fact]
    public async Task BadUploads_AreRejected()
    {
        var (albumId, first, _) = await CreateAlbumAsync();
        var admin = await _factory.CreateAdminAsync();

        var notImage = await admin.Client.PutAsync($"/api/covers/albums/{albumId}", new ByteArrayContent("GIF89a..."u8.ToArray()));
        var tooBig = await admin.Client.PutAsync($"/api/covers/albums/{albumId}", new ByteArrayContent(new byte[CoverImages.MaxBytes + 1]));
        var noAlbum = await admin.Client.PutAsync($"/api/covers/albums/{Guid.NewGuid()}", new ByteArrayContent(CoverFixtures.Png));

        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noAlbum.StatusCode);
        Assert.Empty(Pictures(first));
    }

    [Fact]
    public async Task Search_ReturnsResultsAndProviderStatus()
    {
        var admin = await _factory.CreateAdminAsync();

        var response = await admin.Client.GetFromJsonAsync<CoverSearchResponse>("/api/covers/search?album=Mezzanine&artist=Massive%20Attack");
        var missingAlbum = await admin.Client.GetAsync("/api/covers/search?artist=Massive%20Attack");
        var providers = await admin.Client.GetFromJsonAsync<List<CoverProviderStatusDto>>("/api/covers/providers");

        Assert.Equal(2, response!.Results.Count);
        Assert.Equal(2, response.Providers.Count);
        Assert.Equal(HttpStatusCode.BadRequest, missingAlbum.StatusCode);
        Assert.Equal([CoverSearchService.MusicBrainzId, CoverSearchService.DeezerId], providers!.Select(p => p.Provider));
    }

    [Fact]
    public async Task OnlyAdmins_CanSearchOrChangeCovers()
    {
        var (albumId, first, _) = await CreateAlbumAsync();
        var user = await _factory.CreateUserAsync();
        var anonymous = _factory.CreateClient();

        foreach (var client in new[] { user.Client, anonymous })
        {
            var expected = client == anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await client.GetAsync("/api/covers/providers")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/covers/search?album=Mezzanine")).StatusCode);
            Assert.Equal(expected, (await client.PutAsync($"/api/covers/albums/{albumId}", new ByteArrayContent(CoverFixtures.Png))).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync($"/api/covers/albums/{albumId}/from-search",
                new SetCoverFromSearchRequest("https://coverartarchive.org/release/r1/front-1200.jpg"))).StatusCode);
        }
        Assert.Empty(Pictures(first));
    }

    // Two scanned tracks in one album; the first can start with a back cover.
    private async Task<(Guid AlbumId, string First, string Second)> CreateAlbumAsync(bool firstHasBackCover = false)
    {
        var first = TestMp3.Create(_musicFolder, $"{Guid.NewGuid():N}-01.mp3", new Mp3Tags("One", ["Band"], _album, 2001, 1, []));
        var second = TestMp3.Create(_musicFolder, $"{Guid.NewGuid():N}-02.mp3", new Mp3Tags("Two", ["Band"], _album, 2001, 2, []));
        if (firstHasBackCover)
        {
            using var file = TagLib.File.Create(first);
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(CoverFixtures.Jpeg)) { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg" }];
            file.Save();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryScanner>().ScanAsync([new ScanTarget(_musicFolder)], full: false);
        }

        var albumId = Guid.Empty;
        await _factory.WithDbAsync(async db => albumId = await db.Albums.Where(a => a.Name == _album).Select(a => a.Id).SingleAsync());
        return (albumId, first, second);
    }

    private static TagLib.IPicture[] Pictures(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Pictures;
    }
}

using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Tests;

// The hover cards: library facts at once, the Wikipedia summary (via
// MusicBrainz and Wikidata) on lookup=true, cached on the album or artist.
public sealed class LibraryInfoTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _tag = "t" + Guid.NewGuid().ToString("N")[..10];

    public LibraryInfoTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Artist_FindsTheWikipediaSummaryThroughWikidata_AndCachesIt()
    {
        var name = $"Portishead {_tag}";
        var (artist, _) = await AddAlbumAsync($"Dummy {_tag}", name);
        _factory.Covers.Respond = request => (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("musicbrainz.org", "/ws/2/artist") => Json(new { artists = new[] { new { id = "mb-artist", name, score = 100, country = "GB" } } }),
            ("musicbrainz.org", "/ws/2/artist/mb-artist") => Json(new { relations = new[] { new { type = "wikidata", url = new { resource = "https://www.wikidata.org/wiki/Q191352" } } } }),
            ("www.wikidata.org", "/wiki/Special:EntityData/Q191352.json") => FakeCoverProvidersHandler.Json("""{"entities":{"Q191352":{"sitelinks":{"enwiki":{"title":"Portishead (band)"}}}}}"""),
            ("en.wikipedia.org", var path) when path.Contains("Portishead") => FakeCoverProvidersHandler.Json(
                """{"type":"standard","extract":"Portishead are an English band formed in 1991 in Bristol.","content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Portishead_(band)"}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var user = await _factory.CreateUserClientAsync();

        var before = await user.GetFromJsonAsync<ArtistInfoDto>($"/api/artists/{artist.Id}/info");
        var looked = await user.GetFromJsonAsync<ArtistInfoDto>($"/api/artists/{artist.Id}/info?lookup=true");
        var requests = _factory.Covers.RequestsTo("musicbrainz.org").Count();
        var again = await user.GetFromJsonAsync<ArtistInfoDto>($"/api/artists/{artist.Id}/info?lookup=true");

        Assert.Equal((null, true, 1, 2), (before!.Summary, before.NeedsLookup, before.AlbumCount, before.TrackCount));
        Assert.Equal("Portishead are an English band formed in 1991 in Bristol.", looked!.Summary);
        Assert.Equal(("https://en.wikipedia.org/wiki/Portishead_(band)", "GB", false), (looked.InfoUrl, looked.Country, looked.NeedsLookup));
        // Cached: the second lookup asks nobody.
        Assert.Equal(looked.Summary, again!.Summary);
        Assert.Equal(requests, _factory.Covers.RequestsTo("musicbrainz.org").Count());
    }

    [Fact]
    public async Task Album_UsesADirectWikipediaLink_AndCountsTheCallersPlays()
    {
        var name = $"Mezzanine {_tag}";
        var (_, album) = await AddAlbumAsync(name, $"Massive Attack {_tag}");
        _factory.Covers.Respond = request => (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("musicbrainz.org", "/ws/2/release-group") => Json(new { release_groups = 0 }, $$"""{"release-groups":[{"id":"mb-rg","title":"{{name}}","score":100}]}"""),
            ("musicbrainz.org", "/ws/2/release-group/mb-rg") => Json(new { relations = new[] { new { type = "wikipedia", url = new { resource = "https://en.wikipedia.org/wiki/Mezzanine_(album)" } } } }),
            ("en.wikipedia.org", var path) when path.Contains("Mezzanine") => FakeCoverProvidersHandler.Json("""{"type":"standard","extract":"Mezzanine is the third album by Massive Attack.","content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Mezzanine_(album)"}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var user = await _factory.CreateUserAsync();
        var other = await _factory.CreateUserAsync();
        await _factory.WithDbAsync(async db =>
        {
            var track = await db.MediaItems.FirstAsync(m => m.Albums.Any(al => al.Id == album.Id));
            db.PlayEvents.AddRange(
                new PlayEvent { Id = Guid.NewGuid(), UserId = user.UserId, MediaItemId = track.Id, PlayedAt = DateTime.UtcNow.AddDays(-2), RecordedAt = DateTime.UtcNow },
                new PlayEvent { Id = Guid.NewGuid(), UserId = user.UserId, MediaItemId = track.Id, PlayedAt = DateTime.UtcNow.AddDays(-1), RecordedAt = DateTime.UtcNow },
                new PlayEvent { Id = Guid.NewGuid(), UserId = other.UserId, MediaItemId = track.Id, PlayedAt = DateTime.UtcNow, RecordedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        var info = await user.Client.GetFromJsonAsync<AlbumInfoDto>($"/api/albums/{album.Id}/info?lookup=true");

        Assert.Equal("Mezzanine is the third album by Massive Attack.", info!.Summary);
        Assert.Equal("https://en.wikipedia.org/wiki/Mezzanine_(album)", info.InfoUrl);
        Assert.Equal((2, 1998, 2), (info.TrackCount, info.Year, info.Plays));
        Assert.Equal(TimeSpan.FromMinutes(9), info.Length);
        Assert.Equal([$"Massive Attack {_tag}"], info.Artists);
    }

    [Fact]
    public async Task NothingFound_IsRemembered_UntilAMonthHasPassed()
    {
        var (_, album) = await AddAlbumAsync($"Obscure {_tag}", $"Nobody {_tag}");
        _factory.Covers.Respond = _ => FakeCoverProvidersHandler.Json("""{"release-groups":[]}""");
        var user = await _factory.CreateUserClientAsync();

        var first = await user.GetFromJsonAsync<AlbumInfoDto>($"/api/albums/{album.Id}/info?lookup=true");
        var asked = _factory.Covers.RequestsTo("musicbrainz.org").Count();
        var second = await user.GetFromJsonAsync<AlbumInfoDto>($"/api/albums/{album.Id}/info?lookup=true");
        var askedAgain = _factory.Covers.RequestsTo("musicbrainz.org").Count();
        await _factory.WithDbAsync(async db =>
        {
            var stored = await db.Albums.SingleAsync(al => al.Id == album.Id);
            stored.InfoFetchedAt = DateTime.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        });
        var stale = await user.GetFromJsonAsync<AlbumInfoDto>($"/api/albums/{album.Id}/info");

        Assert.Equal((null, false), (first!.Summary, first.NeedsLookup));
        Assert.Equal(asked, askedAgain);
        Assert.False(second!.NeedsLookup);
        Assert.True(stale!.NeedsLookup);
    }

    [Fact]
    public async Task AFailedLookup_ChangesNothing_SoTheNextHoverTriesAgain()
    {
        // Its own server: the refusal blocks MusicBrainz for a while.
        using var factory = new FossegrimApiFactory();
        factory.Covers.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var artist = new Artist { Id = Guid.NewGuid(), Name = $"Busy {_tag}", DateAdded = DateTime.UtcNow };
        await factory.WithDbAsync(async db =>
        {
            db.Artists.Add(artist);
            await db.SaveChangesAsync();
        });
        var user = await factory.CreateUserClientAsync();

        var info = await user.GetFromJsonAsync<ArtistInfoDto>($"/api/artists/{artist.Id}/info?lookup=true");

        Assert.True(info!.NeedsLookup);
        Assert.Null(info.Summary);
        await factory.WithDbAsync(async db => Assert.Null((await db.Artists.SingleAsync(a => a.Id == artist.Id)).InfoFetchedAt));
    }

    [Fact]
    public async Task UnknownIds_AreNotFound()
    {
        var user = await _factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/albums/{Guid.NewGuid()}/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/artists/{Guid.NewGuid()}/info")).StatusCode);
    }

    // An album by one artist, with two 4.5-minute tracks.
    private async Task<(Artist Artist, Album Album)> AddAlbumAsync(string albumName, string artistName)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = artistName, DateAdded = DateTime.UtcNow };
        var album = new Album { Id = Guid.NewGuid(), Name = albumName, Year = 1998, DateAdded = DateTime.UtcNow };
        album.Artists.Add(artist);
        for (var i = 1; i <= 2; i++)
        {
            var track = new MediaItem
            {
                Id = Guid.NewGuid(),
                Title = $"Track {i} {_tag}",
                ArtistName = artistName,
                Album = albumName,
                Track = i,
                Duration = TimeSpan.FromSeconds(270),
                FileLocation = $"/music/{Guid.NewGuid():N}.mp3",
                DateAdded = DateTime.UtcNow,
            };
            track.Albums.Add(album);
            track.Artists.Add(artist);
            album.MediaItems.Add(track);
        }
        await _factory.WithDbAsync(async db =>
        {
            db.Albums.Add(album);
            await db.SaveChangesAsync();
        });
        return (artist, album);
    }

    private static HttpResponseMessage Json(object value, string? raw = null) =>
        FakeCoverProvidersHandler.Json(raw ?? System.Text.Json.JsonSerializer.Serialize(value));
}

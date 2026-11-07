using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

// Tests share one database, so each makes its own library under a unique
// word (_tag) and searches for it, keeping other tests' rows out.
public sealed class BrowseTests : IClassFixture<FossegrimApiFactory>, IDisposable
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _tag = "t" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _music = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "browse-" + Guid.NewGuid().ToString("N"));

    public BrowseTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_music))
        {
            Directory.Delete(_music, recursive: true);
        }
    }

    [Fact]
    public async Task Search_IgnoresCaseAndAccents_AndMatchesTheStartsOfWords()
    {
        var bjork = Artist($"Björk {_tag}");
        var homogenic = Album($"Homogenic {_tag}", bjork, year: 1997);
        Track($"Jóga {_tag}", homogenic, bjork);
        await SaveAsync(bjork);
        var user = await _factory.CreateUserClientAsync();

        var result = await user.GetFromJsonAsync<SearchResultDto>($"/api/search?q=BJORK+homo+{_tag}");

        Assert.Empty(result!.Artists.Items);
        Assert.Equal($"Homogenic {_tag}", Assert.Single(result.Albums.Items).Name);
        Assert.Equal($"Jóga {_tag}", Assert.Single(result.Tracks.Items).Title);
        var artists = await user.GetFromJsonAsync<PagedResult<ArtistListItemDto>>($"/api/artists?q=bjö+{_tag}");
        Assert.Equal((1, 1, 1), (artists!.TotalCount, artists.Items[0].AlbumCount, artists.Items[0].TrackCount));
    }

    [Fact]
    public async Task Search_PutsTitleMatchesFirst()
    {
        var artist = Artist($"Someone {_tag}");
        var album = Album($"Night Songs {_tag}", artist);
        Track($"Morning {_tag}", album, artist);
        Track($"Night {_tag}", Album($"Other {_tag}", artist), artist);
        await SaveAsync(artist);
        var user = await _factory.CreateUserClientAsync();

        var tracks = await user.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q=night+{_tag}");

        Assert.Equal([$"Night {_tag}", $"Morning {_tag}"], tracks!.Items.Select(t => t.Title!));
    }

    [Fact]
    public async Task Search_TakesFts5SyntaxLiterally()
    {
        var user = await _factory.CreateUserClientAsync();

        var response = await user.GetAsync($"/api/mediaitems?q={Uri.EscapeDataString($"\"NEAR( {_tag} * - OR")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<PagedResult<MediaItemDto>>())!.TotalCount);
    }

    [Fact]
    public async Task Lists_ArePaged()
    {
        var artist = Artist($"Pager {_tag}");
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
        {
            Album($"{name} {_tag}", artist);
        }
        await SaveAsync(artist);
        var user = await _factory.CreateUserClientAsync();

        var last = await user.GetFromJsonAsync<PagedResult<AlbumListItemDto>>($"/api/albums?q={_tag}&sort=name&page=3&pageSize=2");

        Assert.Equal((5, 3, 3, 2), (last!.TotalCount, last.TotalPages, last.Page, last.PageSize));
        Assert.Equal($"E {_tag}", Assert.Single(last.Items).Name);
    }

    [Fact]
    public async Task Albums_SortByNameYearAndDateAdded_EitherWay()
    {
        var artist = Artist($"Sorter {_tag}");
        Album($"beta {_tag}", artist, year: 2001, added: new DateTime(2026, 3, 1));
        Album($"Alpha {_tag}", artist, year: 1999, added: new DateTime(2026, 1, 1));
        Album($"Gamma {_tag}", artist, year: 2010, added: new DateTime(2026, 2, 1));
        await SaveAsync(artist);
        var user = await _factory.CreateUserClientAsync();

        async Task<string[]> Names(string query) =>
            (await user.GetFromJsonAsync<PagedResult<AlbumListItemDto>>($"/api/albums?q={_tag}&{query}"))!.Items.Select(a => a.Name.Split(' ')[0]).ToArray();

        // Names ignore case; years and dates are newest first unless asked otherwise.
        Assert.Equal(["Alpha", "beta", "Gamma"], await Names("sort=name"));
        Assert.Equal(["Gamma", "beta", "Alpha"], await Names("sort=name&order=desc"));
        Assert.Equal(["Gamma", "beta", "Alpha"], await Names("sort=year"));
        Assert.Equal(["Alpha", "beta", "Gamma"], await Names("sort=year&order=asc"));
        Assert.Equal(["beta", "Gamma", "Alpha"], await Names("sort=added"));
    }

    [Fact]
    public async Task ArtistAlbumAndYearSorts_OrderByThoseTags()
    {
        var zed = Artist($"Zed {_tag}");
        var amy = Artist($"amy {_tag}");
        var first = Album($"First {_tag}", zed, year: 1990);
        var second = Album($"Second {_tag}", amy, year: 2020);
        Track($"One {_tag}", first, zed);
        Track($"Two {_tag}", second, amy);
        await SaveAsync(zed);
        await SaveAsync(amy);
        var user = await _factory.CreateUserClientAsync();

        async Task<string[]> Albums(string query) =>
            (await user.GetFromJsonAsync<PagedResult<AlbumListItemDto>>($"/api/albums?q={_tag}&{query}"))!.Items.Select(a => a.Name.Split(' ')[0]).ToArray();
        async Task<string[]> Tracks(string query) =>
            (await user.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q={_tag}&{query}"))!.Items.Select(t => t.Title!.Split(' ')[0]).ToArray();

        Assert.Equal(["Second", "First"], await Albums("sort=artist"));
        Assert.Equal(["Two", "One"], await Tracks("sort=artist"));
        Assert.Equal(["One", "Two"], await Tracks("sort=album"));
        Assert.Equal(["Two", "One"], await Tracks("sort=year"));
        Assert.Equal(["One", "Two"], await Tracks("sort=title"));
    }

    [Fact]
    public async Task PlaySorts_UseOnlyTheCallersListens()
    {
        var artist = Artist($"Player {_tag}");
        var often = Album($"Often {_tag}", artist);
        var lately = Album($"Lately {_tag}", artist);
        var never = Album($"Never {_tag}", artist);
        var oftenTrack = Track($"Often track {_tag}", often, artist);
        var latelyTrack = Track($"Lately track {_tag}", lately, artist);
        Track($"Never track {_tag}", never, artist);
        await SaveAsync(artist);
        var me = await _factory.CreateUserAsync();
        var someoneElse = await _factory.CreateUserAsync();
        await PlaysAsync(me, oftenTrack, new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), new DateTime(2026, 1, 3));
        await PlaysAsync(me, latelyTrack, new DateTime(2026, 6, 1));
        // Not counted for me.
        await PlaysAsync(someoneElse, latelyTrack, new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), new DateTime(2026, 1, 3), new DateTime(2026, 1, 4));

        async Task<string[]> Albums(string sort) =>
            (await me.Client.GetFromJsonAsync<PagedResult<AlbumListItemDto>>($"/api/albums?q={_tag}&sort={sort}"))!.Items.Select(a => a.Name.Split(' ')[0]).ToArray();
        async Task<string[]> Tracks(string sort) =>
            (await me.Client.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q={_tag}&sort={sort}"))!.Items.Select(t => t.Title!.Split(' ')[0]).ToArray();

        Assert.Equal(["Often", "Lately", "Never"], await Albums("played"));
        Assert.Equal(["Lately", "Often", "Never"], await Albums("recent"));
        Assert.Equal(["Often", "Lately", "Never"], await Tracks("played"));
        Assert.Equal(["Lately", "Often", "Never"], await Tracks("recent"));
        var artists = await me.Client.GetFromJsonAsync<PagedResult<ArtistListItemDto>>($"/api/artists?q={_tag}&sort=played");
        Assert.Single(artists!.Items);
    }

    [Theory]
    [InlineData("/api/albums?sort=title", "sort must be one of: name, artist, year, added, played, recent, favorited, relevance.")]
    [InlineData("/api/artists?sort=year", "sort must be one of: name, added, played, recent, favorited, relevance.")]
    [InlineData("/api/mediaitems?sort=title&order=up", "order must be asc or desc.")]
    [InlineData("/api/search?q=+-+", "q must contain a word to search for.")]
    public async Task BadQueries_AreRejected(string url, string error)
    {
        var user = await _factory.CreateUserClientAsync();

        var response = await user.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
    }

    [Fact]
    public async Task ArtistTracks_ComeAlbumByAlbum_OldestFirst()
    {
        var artist = Artist($"Discography {_tag}");
        var later = Album($"Later {_tag}", artist, year: 2005);
        var earlier = Album($"Earlier {_tag}", artist, year: 1995);
        Track("Later two", later, artist, track: 2);
        Track("Later one", later, artist, track: 1);
        Track("Earlier one", earlier, artist, track: 1);
        await SaveAsync(artist);
        var user = await _factory.CreateUserClientAsync();

        var tracks = await user.GetFromJsonAsync<List<MediaItemDto>>($"/api/artists/{artist.Id}/tracks");

        Assert.Equal(["Earlier one", "Later one", "Later two"], tracks!.Select(t => t.Title!));
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/artists/{Guid.NewGuid()}/tracks")).StatusCode);
    }

    [Fact]
    public async Task Index_FollowsScansAndTagEdits()
    {
        var path = TestMp3.Create(_music, "01.mp3", new Mp3Tags($"Scanned {_tag}", ["Band"], $"Record {_tag}", 2001, 1, []));
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryScanner>().ScanAsync([new ScanTarget(_music)], full: false);
        }
        var admin = await _factory.CreateAdminAsync();

        var found = await admin.Client.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q=scanned+{_tag}");
        var track = Assert.Single(found!.Items);

        var edit = await admin.Client.PutAsJsonAsync("/api/mediaitems/tags", new UpdateMediaItemTagsRequest(
            [new MediaItemTagUpdate(track.Id, $"Retitled {_tag}", "Band", $"Record {_tag}", 1, 2001, null)]));
        edit.EnsureSuccessStatusCode();

        Assert.Equal(0, (await admin.Client.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q=scanned+{_tag}"))!.TotalCount);
        Assert.Equal(1, (await admin.Client.GetFromJsonAsync<PagedResult<MediaItemDto>>($"/api/mediaitems?q=retitled+{_tag}"))!.TotalCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task EnsureBuilt_RefillsAnIndexThatFellOutOfStep()
    {
        var artist = Artist($"Restored {_tag}");
        await SaveAsync(artist);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM \"ArtistSearch\""));
        var user = await _factory.CreateUserClientAsync();
        Assert.Equal(0, (await user.GetFromJsonAsync<PagedResult<ArtistListItemDto>>($"/api/artists?q={_tag}"))!.TotalCount);

        await _factory.WithDbAsync(db => new SearchIndex(db).EnsureBuiltAsync());

        Assert.Equal(1, (await user.GetFromJsonAsync<PagedResult<ArtistListItemDto>>($"/api/artists?q={_tag}"))!.TotalCount);
    }

    private static Artist Artist(string name) => new() { Id = Guid.NewGuid(), Name = name, DateAdded = DateTime.UtcNow };

    private static Album Album(string name, Artist artist, int? year = null, DateTime? added = null)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = name, Year = year, DateAdded = added ?? DateTime.UtcNow };
        album.Artists.Add(artist);
        artist.Albums.Add(album);
        return album;
    }

    private static MediaItem Track(string title, Album album, Artist artist, int? track = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            ArtistName = artist.Name,
            Album = album.Name,
            Year = album.Year,
            Track = track,
            FileLocation = $"/music/{Guid.NewGuid():N}.mp3",
            DateAdded = DateTime.UtcNow,
        };
        item.Albums.Add(album);
        item.Artists.Add(artist);
        album.MediaItems.Add(item);
        artist.MediaItems.Add(item);
        return item;
    }

    // Saves the artist with its albums and tracks, then rebuilds the search
    // index, as a scan would.
    private Task SaveAsync(Artist artist) => _factory.WithDbAsync(async db =>
    {
        db.Artists.Add(artist);
        await db.SaveChangesAsync();
        await new SearchIndex(db).RebuildAsync();
    });

    private Task PlaysAsync(TestUser user, MediaItem track, params DateTime[] playedAt) => _factory.WithDbAsync(async db =>
    {
        db.PlayEvents.AddRange(playedAt.Select(at => new PlayEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            MediaItemId = track.Id,
            PlayedAt = at,
            RecordedAt = at,
        }));
        await db.SaveChangesAsync();
    });
}

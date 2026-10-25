using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

public sealed class TagEditingTests : IClassFixture<FossegrimApiFactory>, IDisposable
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _musicFolder = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "music-" + Guid.NewGuid().ToString("N"));

    // Unique per test, since tests share one database and artists/albums are matched by name.
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public TagEditingTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_musicFolder))
        {
            Directory.Delete(_musicFolder, recursive: true);
        }
    }

    [Fact]
    public async Task Scan_ReadsYearTrackAndGenre()
    {
        TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Teardrop", ["Massive Attack"], "Mezzanine", 1998, 3, ["Trip-Hop"]));

        var item = await ScanSingleAsync();

        Assert.Equal(1998, item.Year);
        Assert.Equal(3, item.Track);
        Assert.Equal("Trip-Hop", item.Genre);
    }

    // The original bug: the database didn't have year/track/genre (scanned
    // before the scanner read them), and saving any edit blanked them in the file.
    [Fact]
    public async Task EditingTitle_LeavesTagsTheDatabaseNeverRead()
    {
        var path = TestMp3.Create(_musicFolder, "01.mp3",
            new Mp3Tags("Teardrop", ["Massive Attack", "Elizabeth Fraser"], "Mezzanine", 1998, 3, ["Trip-Hop", "Electronic"]));
        var item = await AddItemAsync(path, title: "Teardrop", artist: "Massive Attack", album: "Mezzanine"); // no year/track/genre

        var result = await UpdateAsync(Edit(item) with { Title = "Teardrop (Remastered)" });

        Assert.Empty(result.Errors);
        var tags = TestMp3.Read(path);
        Assert.Equal("Teardrop (Remastered)", tags.Title);
        Assert.Equal(["Massive Attack", "Elizabeth Fraser"], tags.Performers);
        Assert.Equal(1998u, tags.Year);
        Assert.Equal(3u, tags.Track);
        Assert.Equal(["Trip-Hop", "Electronic"], tags.Genres);
    }

    [Fact]
    public async Task EditingYear_WritesOnlyTheYear()
    {
        var path = TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Teardrop", ["Massive Attack"], "Mezzanine", 1997, 3, ["Trip-Hop"]));
        var item = await ScanSingleAsync();

        await UpdateAsync(Edit(item) with { Year = 1998 });

        var tags = TestMp3.Read(path);
        Assert.Equal(1998u, tags.Year);
        Assert.Equal("Teardrop", tags.Title);
        Assert.Equal(["Massive Attack"], tags.Performers);
        Assert.Equal("Mezzanine", tags.Album);
        Assert.Equal(3u, tags.Track);
        Assert.Equal(["Trip-Hop"], tags.Genres);
        Assert.Equal(1998, (await GetItemAsync(item.Id)).Year);
    }

    [Fact]
    public async Task ClearingAField_RemovesItFromTheFile()
    {
        var path = TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Teardrop", ["Massive Attack"], "Mezzanine", 1998, 3, ["Trip-Hop"]));
        var item = await ScanSingleAsync();

        await UpdateAsync(Edit(item) with { Genre = "  ", Track = null });

        var tags = TestMp3.Read(path);
        Assert.Empty(tags.Genres);
        Assert.Equal(0u, tags.Track);
        var stored = await GetItemAsync(item.Id);
        Assert.Null(stored.Genre);
        Assert.Null(stored.Track);
    }

    [Fact]
    public async Task SavingWithNoChanges_DoesNotRewriteTheFile()
    {
        var path = TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Teardrop", ["Massive Attack"], "Mezzanine", 1998, 3, ["Trip-Hop"]));
        var item = await ScanSingleAsync();
        var before = File.ReadAllBytes(path);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await UpdateAsync(Edit(item));

        Assert.Empty(result.Errors);
        Assert.Single(result.Updated);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task RenamingTheOnlyTracksArtistAndAlbum_RemovesTheEmptyOnes()
    {
        TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Song", [$"Typo Artist {_tag}"], $"Typo Album {_tag}", 2000, 1, []));
        var item = await ScanSingleAsync();

        await UpdateAsync(Edit(item) with { ArtistName = $"Right Artist {_tag}", Album = $"Right Album {_tag}" });

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Artists.AnyAsync(a => a.Name == $"Typo Artist {_tag}"));
            Assert.False(await db.Albums.AnyAsync(a => a.Name == $"Typo Album {_tag}"));
            var artist = await db.Artists.Include(a => a.MediaItems).SingleAsync(a => a.Name == $"Right Artist {_tag}");
            Assert.Equal(item.Id, Assert.Single(artist.MediaItems).Id);
            Assert.True(await db.Albums.AnyAsync(a => a.Name == $"Right Album {_tag}"));
        });
    }

    [Fact]
    public async Task ArtistWithOtherTracks_IsKeptAfterAnEditMovesOneAway()
    {
        TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("One", [$"Shared {_tag}"], $"Album {_tag}", 2000, 1, []));
        TestMp3.Create(_musicFolder, "02.mp3", new Mp3Tags("Two", [$"Shared {_tag}"], $"Album {_tag}", 2000, 2, []));
        var items = await ScanAsync();
        var one = items.Single(i => i.Title == "One");

        await UpdateAsync(Edit(one) with { ArtistName = $"Someone Else {_tag}" });

        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Artists.AnyAsync(a => a.Name == $"Shared {_tag}"));
            Assert.True(await db.Albums.AnyAsync(a => a.Name == $"Album {_tag}"));
        });
    }

    [Fact]
    public async Task MissingFile_IsReportedPerTrack_AndOthersStillSave()
    {
        TestMp3.Create(_musicFolder, "01.mp3", new Mp3Tags("Present", ["A"], "B", 2000, 1, []));
        TestMp3.Create(_musicFolder, "02.mp3", new Mp3Tags("Gone", ["A"], "B", 2000, 2, []));
        var items = await ScanAsync();
        var present = items.Single(i => i.Title == "Present");
        var gone = items.Single(i => i.Title == "Gone");
        File.Delete(gone.FileLocation!);

        var result = await UpdateAsync(Edit(present) with { Year = 2001 }, Edit(gone) with { Year = 2001 });

        var error = Assert.Single(result.Errors);
        Assert.Equal(gone.Id, error.Id);
        Assert.Equal(2001, (await GetItemAsync(present.Id)).Year);
    }

    private static MediaItemTagUpdate Edit(MediaItem item) =>
        new(item.Id, item.Title, item.ArtistName, item.Album, item.Track, item.Year, item.Genre);

    private async Task<UpdateMediaItemTagsResponse> UpdateAsync(params MediaItemTagUpdate[] updates)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MediaLibraryService>().UpdateTagsAsync(updates);
    }

    private async Task<List<MediaItem>> ScanAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaLibraryService>().ScanAndSaveMediaItemsAsync(_musicFolder, maxDegreeOfParallelism: 1);
        }

        List<MediaItem> items = [];
        await _factory.WithDbAsync(async db =>
            items = await db.MediaItems.AsNoTracking().Where(m => m.FileLocation!.StartsWith(_musicFolder)).ToListAsync());
        return items;
    }

    private async Task<MediaItem> ScanSingleAsync() => Assert.Single(await ScanAsync());

    private async Task<MediaItem> AddItemAsync(string path, string title, string artist, string album)
    {
        var item = new MediaItem { Id = Guid.NewGuid(), Title = title, ArtistName = artist, Album = album, FileLocation = path };
        await _factory.WithDbAsync(async db =>
        {
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
        });
        return item;
    }

    private async Task<MediaItem> GetItemAsync(Guid id)
    {
        MediaItem? item = null;
        await _factory.WithDbAsync(async db => item = await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == id));
        return item!;
    }
}

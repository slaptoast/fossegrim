using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

public sealed class LibraryScannerTests : IClassFixture<FossegrimApiFactory>, IDisposable
{
    private readonly FossegrimApiFactory _factory;
    private readonly string _music = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "scan-" + Guid.NewGuid().ToString("N"));

    // Unique per test, since tests share one database and albums are matched by name.
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public LibraryScannerTests(FossegrimApiFactory factory)
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
    public async Task FirstScan_AddsTracksWithTagsDurationAndFileState()
    {
        var path = TestMp3.Create(Path.Combine(_music, "Band"), "01.mp3", Tags("Song", 1));

        var summary = await ScanAsync();

        Assert.Equal((1, 1, 0, 0), (summary.FilesFound, summary.Added, summary.Updated, summary.Unchanged));
        var item = Assert.Single(await ItemsAsync());
        Assert.Equal(("Song", "Band", $"Album {_tag}", 1), (item.Title, item.ArtistName, item.Album, item.Track));
        Assert.NotNull(item.Duration);
        Assert.Equal(128, item.Bitrate);
        Assert.Equal(new FileInfo(path).Length, item.FileSize);
        Assert.Null(item.TagVersionMismatch);
    }

    [Fact]
    public async Task QuickScan_SkipsUnchangedFiles_ButReadsChangedOnes()
    {
        TestMp3.Create(_music, "01.mp3", Tags("One", 1));
        var two = TestMp3.Create(_music, "02.mp3", Tags("Two", 2));
        await ScanAsync();

        // Changed in the database only: a quick scan that skipped the file wouldn't notice.
        await _factory.WithDbAsync(db => db.MediaItems.Where(m => m.FileLocation!.StartsWith(_music)).ExecuteUpdateAsync(u => u.SetProperty(m => m.Genre, "stale")));
        using (var file = TagLib.File.Create(two))
        {
            file.Tag.Title = "Two (edited)";
            file.Save();
        }
        File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(1));

        var quick = await ScanAsync();

        Assert.Equal((2, 0, 1, 1), (quick.FilesFound, quick.Added, quick.Updated, quick.Unchanged));
        var items = await ItemsAsync();
        Assert.Equal("stale", items.Single(i => i.Title == "One").Genre);
        Assert.Null(items.Single(i => i.Title == "Two (edited)").Genre);

        var full = await ScanAsync(full: true);
        Assert.Equal((2, 0), (full.Updated, full.Unchanged));
        Assert.All(await ItemsAsync(), i => Assert.Null(i.Genre));
    }

    [Fact]
    public async Task DeletedFile_IsRemoved_AndItsEmptyAlbumToo()
    {
        TestMp3.Create(_music, "01.mp3", Tags("Keep", 1));
        var gone = TestMp3.Create(_music, "02.mp3", Tags("Gone", 2, album: $"Lonely {_tag}"));
        await ScanAsync();
        File.Delete(gone);

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Removed);
        Assert.Equal("Keep", Assert.Single(await ItemsAsync()).Title);
        await _factory.WithDbAsync(async db => Assert.False(await db.Albums.AnyAsync(a => a.Name == $"Lonely {_tag}")));
    }

    [Fact]
    public async Task MovedFile_KeepsItsIdAndPlayHistory()
    {
        var original = TestMp3.Create(Path.Combine(_music, "old"), "01.mp3", Tags("Wanderer", 1));
        await ScanAsync();
        var item = Assert.Single(await ItemsAsync());
        var user = await _factory.CreateUserAsync();
        await _factory.WithDbAsync(async db =>
        {
            db.PlayEvents.Add(new PlayEvent { Id = Guid.NewGuid(), UserId = user.UserId, MediaItemId = item.Id, PlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        var moved = Path.Combine(_music, "new", "renamed.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);
        var summary = await ScanAsync();

        Assert.Equal((1, 0, 0), (summary.Moved, summary.Added, summary.Removed));
        var after = Assert.Single(await ItemsAsync());
        Assert.Equal(item.Id, after.Id);
        Assert.Equal(moved, after.FileLocation);
        await _factory.WithDbAsync(async db => Assert.True(await db.PlayEvents.AnyAsync(p => p.MediaItemId == item.Id)));
    }

    [Fact]
    public async Task FolderWithNoAudioFiles_KeepsItsTracks_InCaseTheDriveIsUnmounted()
    {
        var path = TestMp3.Create(_music, "01.mp3", Tags("Still here", 1));
        await ScanAsync();
        File.Delete(path);

        var summary = await ScanAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Contains(summary.Warnings, w => w.Contains("kept"));
        Assert.Single(await ItemsAsync());
    }

    [Fact]
    public async Task MissingFolder_IsReported_AndNothingRemoved()
    {
        TestMp3.Create(_music, "01.mp3", Tags("Here", 1));
        await ScanAsync();
        Directory.Delete(_music, recursive: true);

        var summary = await ScanAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Contains(summary.Warnings, w => w.StartsWith("Folder not found"));
        Assert.Single(await ItemsAsync());
    }

    [Fact]
    public async Task NonRecursiveTarget_LeavesSubfoldersAlone()
    {
        TestMp3.Create(_music, "top.mp3", Tags("Top", 1));
        TestMp3.Create(Path.Combine(_music, "sub"), "deep.mp3", Tags("Deep", 2));

        var summary = await ScanAsync(target: new ScanTarget(_music, Recursive: false));

        Assert.Equal(1, summary.FilesFound);
        Assert.Equal("Top", Assert.Single(await ItemsAsync()).Title);
    }

    [Fact]
    public async Task Id3v1DisagreeingWithId3v2_IsRecorded()
    {
        var path = TestMp3.Create(_music, "01.mp3", Tags("Right title", 1));
        TestMp3.SetId3v1(path, title: "Wrong title", year: 1971);

        await ScanAsync();

        Assert.Equal("title,year", Assert.Single(await ItemsAsync()).TagVersionMismatch);
    }

    [Fact]
    public async Task ScanProgress_CountsThenReadsEveryFile()
    {
        for (var i = 1; i <= 5; i++)
        {
            TestMp3.Create(_music, $"{i:00}.mp3", Tags($"Track {i}", (uint)i));
        }
        var reports = new List<ScanProgress>();

        await ScanAsync(progress: new SynchronousProgress(reports));

        Assert.Equal(ScanPhase.Counting, reports.First().Phase);
        Assert.Contains(reports, r => r is { Phase: ScanPhase.Reading, Processed: 5, Total: 5 });
        Assert.Equal(ScanPhase.Done, reports.Last().Phase);
    }

    private Mp3Tags Tags(string title, uint track, string? album = null) =>
        new(title, ["Band"], album ?? $"Album {_tag}", 2001, track, []);

    private async Task<ScanSummary> ScanAsync(bool full = false, ScanTarget? target = null, IProgress<ScanProgress>? progress = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryScanner>()
            .ScanAsync([target ?? new ScanTarget(_music)], full, progress);
    }

    private async Task<List<MediaItem>> ItemsAsync()
    {
        List<MediaItem> items = [];
        await _factory.WithDbAsync(async db =>
            items = await db.MediaItems.AsNoTracking().Where(m => m.FileLocation!.StartsWith(_music)).OrderBy(m => m.FileLocation).ToListAsync());
        return items;
    }

    private sealed class SynchronousProgress(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}

public class FileNameTemplateTests
{
    private static readonly FileNameTags Teardrop = new("Massive Attack", "Mezzanine", "Teardrop", 3, 1998, "Trip-Hop");

    [Fact]
    public void Render_FillsTokensAndKeepsTheExtension()
    {
        Assert.Equal("Massive Attack/Mezzanine/03 - Teardrop.flac", FileNameTemplate.Render(FileNameTemplate.Default, Teardrop, ".FLAC"));
        Assert.Equal("1998 - Mezzanine/3 Teardrop.mp3", FileNameTemplate.Render("{year} - {album}/{track} {title}", Teardrop, ".mp3"));
    }

    [Fact]
    public void Render_ReplacesCharactersFilesCantHave()
    {
        var tags = Teardrop with { Artist = "AC/DC", Title = "What? Who: \"Me\"*" };
        // A replaced character at the end is dropped, so "Why?" is "Why", not "Why_".
        Assert.Equal("AC_DC/Mezzanine/03 - What_ Who_ _Me.mp3", FileNameTemplate.Render(FileNameTemplate.Default, tags, ".mp3"));
    }

    [Fact]
    public void Render_FallsBackForMissingTags_AndDropsDanglingSeparators()
    {
        var tags = new FileNameTags(null, null, "Loose", null, null, null);
        Assert.Equal("Unknown Artist/Unknown Album/Loose.mp3", FileNameTemplate.Render(FileNameTemplate.Default, tags, ".mp3"));
    }

    [Fact]
    public void Render_DoesNotLetTagsEscapeTheFolder()
    {
        var tags = Teardrop with { Artist = "..", Album = "." };
        Assert.Equal("Unknown Artist/Unknown Album/03 - Teardrop.mp3", FileNameTemplate.Render(FileNameTemplate.Default, tags, ".mp3"));
    }

    [Theory]
    [InlineData("{artist}/{album}/{track:00} - {title}", true)]
    [InlineData("{title}", true)]
    [InlineData("{artist}/{album}", false)]            // no title: every track would collide
    [InlineData("{artist}/{nope}/{title}", false)]     // unknown token
    [InlineData("{artist:00}/{title}", false)]         // format on a text token
    [InlineData("/{artist}/{title}", false)]           // rooted
    [InlineData("{artist}/../{title}", false)]         // escapes the folder
    [InlineData("", false)]
    public void Validate_AcceptsOnlyUsableTemplates(string template, bool valid)
    {
        Assert.Equal(valid, FileNameTemplate.Validate(template) is null);
    }
}

public class TagReaderTests
{
    private readonly string _music = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "tags-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LongTitles_OnlyHaveToMatchWhatId3v1CanHold()
    {
        var title = new string('x', 45);
        var path = TestMp3.Create(_music, "long.mp3", new Mp3Tags(title, ["A"], "B", 2001, 1, []));
        TestMp3.SetId3v1(path, title: title[..30]);

        Assert.Null(TagReader.Read(path).TagVersionMismatch);
    }

    [Fact]
    public void SyncId3v1FromId3v2_MakesThemAgree()
    {
        var path = TestMp3.Create(_music, "fix.mp3", new Mp3Tags("Right", ["Artist"], "Album", 2001, 4, ["Rock"]));
        TestMp3.SetId3v1(path, title: "Wrong", year: 1980);
        Assert.NotNull(TagReader.Read(path).TagVersionMismatch);

        TagReader.SyncId3v1FromId3v2(path);

        Assert.Null(TagReader.Read(path).TagVersionMismatch);
        using var file = TagLib.File.Create(path);
        var v1 = file.GetTag(TagLib.TagTypes.Id3v1);
        Assert.Equal(("Right", 2001u), (v1.Title, v1.Year));
    }
}

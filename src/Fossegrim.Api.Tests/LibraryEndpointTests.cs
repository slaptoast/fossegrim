using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Tests;

/// <summary>A media folder of real MP3s registered with the API, deleted afterwards.</summary>
public sealed class TestLibrary : IDisposable
{
    public TestLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "fossegrim-tests", "library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public Guid FolderId { get; private set; }

    public async Task RegisterAsync(FossegrimApiFactory factory)
    {
        var folder = new MediaFolder { Name = "Test " + Path.GetFileName(Root), Location = Root, DateAdded = DateTime.UtcNow };
        await factory.WithDbAsync(async db =>
        {
            db.MediaFolders.Add(folder);
            await db.SaveChangesAsync();
        });
        FolderId = folder.Id;
    }

    public string Add(string relativePath, Mp3Tags tags) =>
        TestMp3.Create(Path.Combine(Root, Path.GetDirectoryName(relativePath) ?? ""), Path.GetFileName(relativePath), tags);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

public sealed class LibraryEndpointTests : IClassFixture<FossegrimApiFactory>, IDisposable
{
    private readonly FossegrimApiFactory _factory;
    private readonly TestLibrary _library = new();
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public LibraryEndpointTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    public void Dispose() => _library.Dispose();

    [Fact]
    public async Task StartScan_RunsInTheBackground_AndReportsTheResult()
    {
        var admin = await SetUpAsync();
        _library.Add("a/01.mp3", Tags("One", 1));
        _library.Add("a/02.mp3", Tags("Two", 2));

        var response = await admin.Client.PostAsJsonAsync("/api/admin/scans", new StartScanRequest(FolderIds: [_library.FolderId]));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await WaitAsync(admin.Client, (await response.Content.ReadFromJsonAsync<ScanJobDto>())!.Id);
        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Equal((2, 2, 2), (job.Summary!.FilesFound, job.Summary.Added, job.Total));
        Assert.Equal("admin", job.Source);
    }

    [Fact]
    public async Task StartScan_PathOutsideMediaFolders_IsRejected()
    {
        var admin = await SetUpAsync();

        var response = await admin.Client.PostAsJsonAsync("/api/admin/scans", new StartScanRequest(Path: Path.GetTempPath()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RescanAlbum_ReReadsOnlyItsFolder()
    {
        var admin = await SetUpAsync();
        _library.Add("album/01.mp3", Tags("In album", 1));
        _library.Add("other/01.mp3", Tags("Elsewhere", 1, album: $"Other {_tag}"));
        await ScanAsync(admin.Client);
        var albumId = Guid.Empty;
        await _factory.WithDbAsync(async db => albumId = await db.Albums.Where(a => a.Name == $"Album {_tag}").Select(a => a.Id).SingleAsync());

        var response = await admin.Client.PostAsync($"/api/admin/scans/albums/{albumId}", null);

        var job = await WaitAsync(admin.Client, (await response.Content.ReadFromJsonAsync<ScanJobDto>())!.Id);
        Assert.True(job.Full);
        Assert.Equal((1, 1), (job.Summary!.FilesFound, job.Summary.Updated));
    }

    [Fact]
    public async Task ScanEndpoints_AreAdminOnly()
    {
        await SetUpAsync();
        var user = await _factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/scans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/scans", new StartScanRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/rename/preview", new RenameRequest(All: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/library-health")).StatusCode);
    }

    [Fact]
    public async Task ApiKey_StartsScans_AndCanDoNothingElse()
    {
        var admin = await SetUpAsync();
        _library.Add("x/01.mp3", Tags("Via API", 1));
        var created = await (await admin.Client.PostAsJsonAsync("/api/admin/api-keys", new CreateApiKeyRequest("Lidarr"))).Content.ReadFromJsonAsync<CreatedApiKeyDto>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created!.Secret);

        var response = await client.PostAsJsonAsync("/api/external/scan", new ExternalScanRequest(Path: Path.Combine(_library.Root, "x")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<ScanJobDto>())!;
        Assert.Equal("api:Lidarr", queued.Source);
        var job = await WaitAsync(client, queued.Id, external: true);
        Assert.Equal(1, job.Summary!.Added);

        // The key isn't an admin, or a user at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/scans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // Only the prefix is ever listed again.
        var keys = await admin.Client.GetFromJsonAsync<List<ApiKeyDto>>("/api/admin/api-keys");
        var listed = Assert.Single(keys!, k => k.Id == created.Key.Id);
        Assert.StartsWith(listed.Prefix, created.Secret);
        Assert.NotNull(listed.LastUsedAt);
    }

    [Fact]
    public async Task ExternalScan_RejectsMissingWrongAndRevokedKeys_AndAdminTokens()
    {
        var admin = await SetUpAsync();
        var created = await (await admin.Client.PostAsJsonAsync("/api/admin/api-keys", new CreateApiKeyRequest("Temp"))).Content.ReadFromJsonAsync<CreatedApiKeyDto>();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync("/api/external/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Client.PostAsync("/api/external/scan", null)).StatusCode);

        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", created!.Secret + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsync("/api/external/scan", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/admin/api-keys/{created.Key.Id}")).StatusCode);
        var revoked = _factory.CreateClient();
        revoked.DefaultRequestHeaders.Add("X-Api-Key", created.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/external/scan")).StatusCode);
    }

    [Fact]
    public async Task Rename_MovesFilesToTheTemplate_TakingCoversAlong_AndTidiesUp()
    {
        var admin = await SetUpAsync();
        _library.Add("Downloads/messy/track1.mp3", Tags("First Song", 1));
        _library.Add("Downloads/messy/track2.mp3", Tags("Second Song", 2));
        File.WriteAllText(Path.Combine(_library.Root, "Downloads", "messy", "cover.jpg"), "jpeg");
        await ScanAsync(admin.Client);
        var ids = await IdsAsync();

        var preview = await (await admin.Client.PostAsJsonAsync("/api/admin/rename/preview", new RenameRequest(ids))).Content.ReadFromJsonAsync<RenamePlanDto>();
        Assert.All(preview!.Entries, e => Assert.Equal(RenameStatus.Rename, e.Status));
        Assert.True(File.Exists(Path.Combine(_library.Root, "Downloads", "messy", "track1.mp3")));

        var result = await (await admin.Client.PostAsJsonAsync("/api/admin/rename", new RenameRequest(ids))).Content.ReadFromJsonAsync<RenameResultDto>();

        Assert.Equal(2, result!.Renamed);
        var albumFolder = Path.Combine(_library.Root, "Band", $"Album {_tag}");
        Assert.True(File.Exists(Path.Combine(albumFolder, "01 - First Song.mp3")));
        Assert.True(File.Exists(Path.Combine(albumFolder, "02 - Second Song.mp3")));
        Assert.True(File.Exists(Path.Combine(albumFolder, "cover.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_library.Root, "Downloads")));
        await _factory.WithDbAsync(async db =>
            Assert.All(await db.MediaItems.Where(m => ids.Contains(m.Id)).ToListAsync(), m => Assert.StartsWith(albumFolder, m.FileLocation)));

        // A quick scan afterwards finds nothing to do.
        var job = await ScanAsync(admin.Client);
        Assert.Equal((0, 0, 0, 2), (job.Summary!.Added, job.Summary.Removed, job.Summary.Updated, job.Summary.Unchanged));
    }

    [Fact]
    public async Task Rename_SkipsConflicts_AndNeverOverwrites()
    {
        var admin = await SetUpAsync();
        _library.Add("in/a.mp3", Tags("Same", 1));
        _library.Add("in/b.mp3", Tags("Same", 1));   // would get the same name as a.mp3
        _library.Add("in/c.mp3", Tags("Taken", 2));
        var squatter = Path.Combine(_library.Root, "Band", $"Album {_tag}", "02 - Taken.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(squatter)!);
        File.WriteAllText(squatter, "not ours");
        await ScanAsync(admin.Client, path: Path.Combine(_library.Root, "in"));
        var ids = await IdsAsync(under: Path.Combine(_library.Root, "in"));

        var result = await (await admin.Client.PostAsJsonAsync("/api/admin/rename", new RenameRequest(ids))).Content.ReadFromJsonAsync<RenameResultDto>();

        Assert.Equal(0, result!.Renamed);
        Assert.Equal(3, result.Problems.Count);
        Assert.All(result.Problems, p => Assert.Equal(RenameStatus.Conflict, p.Status));
        Assert.Equal("not ours", File.ReadAllText(squatter));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_library.Root, "in")).Length);
    }

    [Fact]
    public async Task RenameTemplate_IsValidatedAndSaved()
    {
        var admin = await SetUpAsync();

        var bad = await admin.Client.PutAsJsonAsync("/api/admin/rename/template", new UpdateRenameTemplateRequest("{artist}/{album}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await admin.Client.PutAsJsonAsync("/api/admin/rename/template", new UpdateRenameTemplateRequest("{artist} - {title}"));
        var saved = await good.Content.ReadFromJsonAsync<RenameTemplateDto>();
        Assert.Equal("Massive Attack - Teardrop.mp3", saved!.Example);

        await admin.Client.PutAsJsonAsync("/api/admin/rename/template", new UpdateRenameTemplateRequest("{artist}/{album}/{track:00} - {title}"));
    }

    [Fact]
    public async Task TagVersionReport_ListsAndFixesDisagreements()
    {
        var admin = await SetUpAsync();
        var path = _library.Add("v/01.mp3", Tags("Proper", 1));
        TestMp3.SetId3v1(path, title: "Improper");
        await ScanAsync(admin.Client);
        var id = Assert.Single(await IdsAsync());

        var report = await admin.Client.GetFromJsonAsync<List<TagVersionMismatchDto>>("/api/admin/library-health/tag-versions");
        var entry = Assert.Single(report!, r => r.MediaItemId == id);
        Assert.Equal(["title"], entry.Fields);

        var fix = await (await admin.Client.PostAsJsonAsync("/api/admin/library-health/tag-versions/fix", new FixTagVersionsRequest([id]))).Content.ReadFromJsonAsync<FixTagVersionsResultDto>();

        Assert.Equal(1, fix!.Fixed);
        report = await admin.Client.GetFromJsonAsync<List<TagVersionMismatchDto>>("/api/admin/library-health/tag-versions");
        Assert.DoesNotContain(report!, r => r.MediaItemId == id);
        using var file = TagLib.File.Create(path);
        Assert.Equal("Proper", file.GetTag(TagLib.TagTypes.Id3v1).Title);
    }

    private Mp3Tags Tags(string title, uint track, string? album = null) =>
        new(title, ["Band"], album ?? $"Album {_tag}", 2001, track, []);

    private async Task<TestUser> SetUpAsync()
    {
        await _library.RegisterAsync(_factory);
        return await _factory.CreateAdminAsync();
    }

    private async Task<ScanJobDto> ScanAsync(HttpClient client, string? path = null)
    {
        var request = path is null ? new StartScanRequest(FolderIds: [_library.FolderId]) : new StartScanRequest(Path: path);
        var response = await client.PostAsJsonAsync("/api/admin/scans", request);
        response.EnsureSuccessStatusCode();
        return await WaitAsync(client, (await response.Content.ReadFromJsonAsync<ScanJobDto>())!.Id);
    }

    private static async Task<ScanJobDto> WaitAsync(HttpClient client, Guid jobId, bool external = false)
    {
        var url = external ? $"/api/external/scan/{jobId}" : $"/api/admin/scans/{jobId}";
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<ScanJobDto>(url);
            if (job!.State is ScanJobState.Completed or ScanJobState.Failed)
            {
                Assert.Null(job.Error);
                return job;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The scan didn't finish.");
    }

    private async Task<List<Guid>> IdsAsync(string? under = null)
    {
        var root = under ?? _library.Root;
        List<Guid> ids = [];
        await _factory.WithDbAsync(async db =>
            ids = await db.MediaItems.Where(m => m.FileLocation!.StartsWith(root)).Select(m => m.Id).ToListAsync());
        return ids;
    }
}

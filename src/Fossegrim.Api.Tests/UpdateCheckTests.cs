using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Hosting;

namespace Fossegrim.Api.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3-rc.1", true)]
    [InlineData("1.2", false)]   // floating tags
    [InlineData("1", false)]
    [InlineData("latest", false)]
    [InlineData("sha-1ec41d3", false)]
    [InlineData("pr-12", false)]
    [InlineData("dev", false)]
    public void TryParse_AcceptsOnlyFullReleaseVersions(string text, bool expected)
    {
        Assert.Equal(expected, ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.2.10", "1.2.9")]   // numeric, not string, comparison
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.2.0", "1.2.0-rc.1")] // a release beats its prereleases
    [InlineData("1.2.0-rc.2", "1.2.0-rc.1")]
    public void CompareTo_OrdersReleases(string newer, string older)
    {
        ReleaseVersion.TryParse(newer, out var a);
        ReleaseVersion.TryParse(older, out var b);
        Assert.True(a > b);
        Assert.True(b < a);
    }
}

/// <summary>The API as a release build, so it has a version to compare.</summary>
public class ReleaseApiFactory : FossegrimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("APP_VERSION", "1.2.0");
    }
}

public class UpdateCheckTests : IClassFixture<ReleaseApiFactory>
{
    private readonly ReleaseApiFactory _factory;

    public UpdateCheckTests(ReleaseApiFactory factory)
    {
        _factory = factory;
        _factory.DockerHub.FailWith = null;
    }

    [Fact]
    public async Task Check_FindsNewestStableRelease_IgnoringFloatingAndPrereleaseTags()
    {
        var admin = await _factory.CreateAdminAsync();
        _factory.DockerHub.Pages =
        [
            ["latest", "2.0.0-beta.1", "1.10.0", "1.10", "1", "sha-abc1234"],
            ["1.9.3", "1.2.0", "pr-7"]
        ];

        var status = await CheckAsync(admin.Client);

        Assert.Equal("1.2.0", status.CurrentVersion);
        Assert.Equal("1.10.0", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Null(status.Error);
        Assert.NotNull(status.CheckedAt);
        Assert.Equal("https://hub.docker.com/r/slaptoast/fossegrim/tags?name=1.10.0", status.ReleaseUrl);
    }

    [Fact]
    public async Task Check_UpToDate_WhenLatestIsTheRunningVersion()
    {
        var admin = await _factory.CreateAdminAsync();
        _factory.DockerHub.Pages = [["latest", "1.2.0", "1.1.5"]];

        var status = await CheckAsync(admin.Client);

        Assert.Equal("1.2.0", status.LatestVersion);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task Check_Failure_ReportsErrorAndKeepsLastKnownRelease()
    {
        var admin = await _factory.CreateAdminAsync();
        _factory.DockerHub.Pages = [["1.3.0"]];
        await CheckAsync(admin.Client);

        _factory.DockerHub.FailWith = HttpStatusCode.ServiceUnavailable;
        var status = await CheckAsync(admin.Client);

        Assert.NotNull(status.Error);
        Assert.Equal("1.3.0", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public async Task AutomaticChecks_CanBeTurnedOffAndOn()
    {
        var admin = await _factory.CreateAdminAsync();

        var status = await admin.Client.GetFromJsonAsync<UpdateStatusDto>("/api/admin/updates");
        Assert.True(status!.Enabled);

        var off = await admin.Client.PutAsJsonAsync("/api/admin/updates/settings", new UpdateCheckSettingsRequest(false));
        Assert.False((await off.Content.ReadFromJsonAsync<UpdateStatusDto>())!.Enabled);
        Assert.False((await admin.Client.GetFromJsonAsync<UpdateStatusDto>("/api/admin/updates"))!.Enabled);

        // A manual check still works with automatic checks off.
        _factory.DockerHub.Pages = [["1.2.0"]];
        Assert.Equal("1.2.0", (await CheckAsync(admin.Client)).LatestVersion);

        var on = await admin.Client.PutAsJsonAsync("/api/admin/updates/settings", new UpdateCheckSettingsRequest(true));
        Assert.True((await on.Content.ReadFromJsonAsync<UpdateStatusDto>())!.Enabled);
    }

    [Fact]
    public async Task UpdateEndpoints_AreAdminOnly()
    {
        var user = await _factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/admin/updates/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/updates/settings", new UpdateCheckSettingsRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/updates")).StatusCode);
    }

    private static async Task<UpdateStatusDto> CheckAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/admin/updates/check", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UpdateStatusDto>())!;
    }
}

// The default factory has no APP_VERSION, like a local or `compose --build` image.
public class DevelopmentBuildUpdateCheckTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public DevelopmentBuildUpdateCheckTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DevelopmentBuild_NeverContactsDockerHub()
    {
        var admin = await _factory.CreateAdminAsync();

        var status = await admin.Client.GetFromJsonAsync<UpdateStatusDto>("/api/admin/updates");
        Assert.Equal("dev", status!.CurrentVersion);
        Assert.False(status.CanCheck);
        Assert.False(status.UpdateAvailable);

        var check = await admin.Client.PostAsync("/api/admin/updates/check", null);
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Empty(_factory.DockerHub.Requests);
    }

    [Fact]
    public async Task Version_IsPublic()
    {
        var version = await _factory.CreateClient().GetFromJsonAsync<VersionDto>("/api/version");
        Assert.Equal("dev", version!.Version);
    }
}

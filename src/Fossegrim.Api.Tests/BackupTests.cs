using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Fossegrim.Api.Tests;

/// <summary>The API with a controllable clock, for backup timing.</summary>
public class BackupApiFactory : FossegrimApiFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-01-01T10:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

// Tests in this class share one backup folder, so each starts by emptying it.
public class BackupTests : IClassFixture<BackupApiFactory>
{
    private readonly BackupApiFactory _factory;

    public BackupTests(BackupApiFactory factory)
    {
        _factory = factory;
        if (Directory.Exists(_factory.BackupDirectory))
        {
            Directory.Delete(_factory.BackupDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BackUpNow_WritesZipWithDatabaseKeysAndManifest()
    {
        var admin = await _factory.CreateAdminAsync();
        // Make sure a Data Protection key exists to be backed up.
        _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");

        var status = await RunBackupAsync(admin.Client);

        Assert.True(status.LastRun!.Success);
        Assert.Equal("manual", status.LastRun.Trigger);
        var backup = Assert.Single(status.Backups);
        Assert.Equal(status.LastRun.FileName, backup.FileName);
        Assert.Equal($"fossegrim-backup-{_factory.Time.GetUtcNow():yyyyMMdd-HHmmss}.zip", backup.FileName);

        using var zip = ZipFile.OpenRead(Path.Combine(_factory.BackupDirectory, backup.FileName));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("dataprotection-keys/") && e.FullName.EndsWith(".xml"));

        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            Assert.Equal("Fossegrim", manifest.RootElement.GetProperty("app").GetString());
            Assert.Equal("manual", manifest.RootElement.GetProperty("trigger").GetString());
            Assert.False(string.IsNullOrEmpty(manifest.RootElement.GetProperty("lastMigration").GetString()));
        }

        // The database copy is a working SQLite database with this admin in it.
        var databaseCopy = Path.Combine(Path.GetTempPath(), $"fossegrim-restore-{Guid.NewGuid():N}.db");
        zip.GetEntry("fossegrim.db")!.ExtractToFile(databaseCopy);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databaseCopy};Pooling=False");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AspNetUsers WHERE Id = $id";
            command.Parameters.AddWithValue("$id", admin.UserId);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(databaseCopy);
        }
    }

    [Fact]
    public async Task Retention_KeepsTheNewestAndLeavesOtherFilesAlone()
    {
        var admin = await _factory.CreateAdminAsync();
        Directory.CreateDirectory(_factory.BackupDirectory);
        var now = _factory.Time.GetUtcNow();
        string Name(DateTimeOffset at) => $"fossegrim-backup-{at:yyyyMMdd-HHmmss}.zip";
        foreach (var daysAgo in new[] { 1, 5, 15, 30 })
        {
            File.WriteAllText(Path.Combine(_factory.BackupDirectory, Name(now.AddDays(-daysAgo))), "old");
        }
        File.WriteAllText(Path.Combine(_factory.BackupDirectory, "notes.txt"), "not ours");
        File.WriteAllText(Path.Combine(_factory.BackupDirectory, "fossegrim-backup-old.zip"), "not ours either");

        await SaveScheduleAsync(admin.Client, BackupScheduleDto.Default with { KeepLast = 4, MaxAgeDays = 10 });
        var status = await RunBackupAsync(admin.Client);

        // Keep 4 would leave the 15-day-old one too, but it's over 10 days old.
        Assert.Equal(
            [Name(now), Name(now.AddDays(-1)), Name(now.AddDays(-5))],
            status.Backups.Select(b => b.FileName));
        Assert.True(File.Exists(Path.Combine(_factory.BackupDirectory, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_factory.BackupDirectory, "fossegrim-backup-old.zip")));
    }

    [Fact]
    public async Task Retention_NeverDeletesTheNewestBackup()
    {
        var admin = await _factory.CreateAdminAsync();
        await SaveScheduleAsync(admin.Client, BackupScheduleDto.Default with { KeepLast = 1, MaxAgeDays = 1 });

        await RunBackupAsync(admin.Client);
        _factory.Time.Advance(TimeSpan.FromDays(3));
        var status = await RunBackupAsync(admin.Client);

        Assert.Equal(status.LastRun!.FileName, Assert.Single(status.Backups).FileName);
    }

    [Fact]
    public async Task DownloadAndDelete_OnlyTouchFossegrimBackups()
    {
        var admin = await _factory.CreateAdminAsync();
        var status = await RunBackupAsync(admin.Client);
        var fileName = status.LastRun!.FileName!;
        File.WriteAllText(Path.Combine(_factory.BackupDirectory, "secret.zip"), "nope");

        var download = await admin.Client.GetAsync($"/api/admin/backups/files/{fileName}");
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new FileInfo(Path.Combine(_factory.BackupDirectory, fileName)).Length, (await download.Content.ReadAsByteArrayAsync()).Length);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/api/admin/backups/files/secret.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/api/admin/backups/files/..%2F..%2Ffossegrim.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.DeleteAsync("/api/admin/backups/files/secret.zip")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/admin/backups/files/{fileName}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(_factory.BackupDirectory, fileName)));
        Assert.True(File.Exists(Path.Combine(_factory.BackupDirectory, "secret.zip")));
    }

    [Fact]
    public async Task Schedule_IsSavedAndDrivesNextRun()
    {
        var admin = await _factory.CreateAdminAsync();
        var now = _factory.Time.GetUtcNow().UtcDateTime;

        var status = await SaveScheduleAsync(admin.Client, BackupScheduleDto.Default with
        {
            Enabled = true,
            Frequency = BackupFrequency.Weekly,
            DaysOfWeek = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday],
            TimeOfDay = new TimeOnly(4, 15),
            TimeZone = "UTC"
        });

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], status.Schedule.DaysOfWeek);
        Assert.Equal(new TimeOnly(4, 15), status.Schedule.TimeOfDay);
        // The next Monday or Friday at 04:15 after the save.
        var expected = Enumerable.Range(1, 7).Select(d => now.Date.AddDays(d).AddHours(4).AddMinutes(15))
            .First(d => d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Friday);
        Assert.Equal(expected, status.NextRunAt);

        await SaveScheduleAsync(admin.Client, status.Schedule with { Enabled = false });
        Assert.Null((await admin.Client.GetFromJsonAsync<BackupStatusDto>("/api/admin/backups"))!.NextRunAt);
    }

    [Fact]
    public async Task NextRun_AfterAMissedRun_IsDueImmediately()
    {
        var admin = await _factory.CreateAdminAsync();
        await SaveScheduleAsync(admin.Client, BackupScheduleDto.Default with
        {
            Enabled = true,
            Frequency = BackupFrequency.Interval,
            IntervalHours = 6
        });
        await RunBackupAsync(admin.Client);
        var lastRun = _factory.Time.GetUtcNow().UtcDateTime;

        // The server was "down" for a day: the next run is in the past, which the worker treats as due now.
        _factory.Time.Advance(TimeSpan.FromDays(1));
        using var scope = _factory.Services.CreateScope();
        var next = await scope.ServiceProvider.GetRequiredService<BackupService>().GetNextRunAsync();

        Assert.Equal(lastRun.AddHours(6), next);
        Assert.True(next < _factory.Time.GetUtcNow().UtcDateTime);
    }

    [Theory]
    [InlineData(0, 1, null, "UTC")]
    [InlineData(24, 32, null, "UTC")]
    [InlineData(24, 1, 0, "UTC")]
    [InlineData(24, 1, null, "Mars/Olympus_Mons")]
    public async Task Schedule_RejectsInvalidValues(int intervalHours, int dayOfMonth, int? keepLast, string timeZone)
    {
        var admin = await _factory.CreateAdminAsync();
        var response = await admin.Client.PutAsJsonAsync("/api/admin/backups/schedule", BackupScheduleDto.Default with
        {
            IntervalHours = intervalHours,
            DayOfMonth = dayOfMonth,
            KeepLast = keepLast,
            TimeZone = timeZone
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BackupEndpoints_AreAdminOnly()
    {
        var user = await _factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/admin/backups", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/backups/schedule", BackupScheduleDto.Default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/backups/files/fossegrim-backup-20260101-100000.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/backups")).StatusCode);
    }

    private static async Task<BackupStatusDto> RunBackupAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/admin/backups", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BackupStatusDto>())!;
    }

    private static async Task<BackupStatusDto> SaveScheduleAsync(HttpClient client, BackupScheduleDto schedule)
    {
        var response = await client.PutAsJsonAsync("/api/admin/backups/schedule", schedule);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BackupStatusDto>())!;
    }
}

using Fossegrim.Lib.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fossegrim.Api.Tests;

// AppSessionsAndDiscs adds disc numbers, which only a scan can fill in, so
// existing tracks must be re-read by the next quick scan.
public sealed class DiscMigrationTests : IDisposable
{
    private const string MigrationBefore = "20261031162034_LibraryScanning";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "fossegrim-tests", $"migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task ExistingTracks_AreMarkedForReReading_AndKeepTheirSize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "MediaItems" ("Id", "Title", "FileLocation", "FileSize", "FileModifiedAt")
                VALUES ('AAAAAAAA-0000-0000-0000-000000000001', 'A', '/a.mp3', 4170, '2026-10-01 12:00:00');
                """);
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();

            var item = await db.MediaItems.SingleAsync();
            Assert.Equal((4170L, (DateTime?)null, (int?)null), (item.FileSize!.Value, item.FileModifiedAt, item.Disc));
        }
    }

    private FossegrimDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FossegrimDbContext>().UseSqlite($"Data Source={_path}").Options);
}

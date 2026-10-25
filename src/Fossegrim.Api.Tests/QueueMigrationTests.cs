using Fossegrim.Lib.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fossegrim.Api.Tests;

// QueueCurrentItem turns the queue's "now playing" track id into a queue
// entry id. Existing databases must come through with the pointer intact.
public sealed class QueueMigrationTests : IDisposable
{
    private const string MigrationBefore = "20261025161206_AddScrobbleTracking";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "fossegrim-tests", $"migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task ExistingQueuePointer_MovesToTheFirstEntryForThatTrack()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "DateJoined", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('u1', '2026-01-01', 0, 0, 0, 0, 0);
                INSERT INTO "MediaItems" ("Id", "Title", "FileLocation") VALUES
                    ('AAAAAAAA-0000-0000-0000-000000000001', 'A', '/a.mp3'),
                    ('AAAAAAAA-0000-0000-0000-000000000002', 'B', '/b.mp3');
                -- Queue: B, A, A, with A "now playing"; the pointer should land on the first A.
                INSERT INTO "Playlists" ("Id", "Name", "Type", "OwnerId", "CurrentMediaItemId")
                VALUES ('BBBBBBBB-0000-0000-0000-000000000001', 'Queue', 1, 'u1', 'AAAAAAAA-0000-0000-0000-000000000001');
                INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "MediaItemId", "Position") VALUES
                    ('CCCCCCCC-0000-0000-0000-000000000001', 'BBBBBBBB-0000-0000-0000-000000000001', 'AAAAAAAA-0000-0000-0000-000000000002', 0),
                    ('CCCCCCCC-0000-0000-0000-000000000002', 'BBBBBBBB-0000-0000-0000-000000000001', 'AAAAAAAA-0000-0000-0000-000000000001', 1000),
                    ('CCCCCCCC-0000-0000-0000-000000000003', 'BBBBBBBB-0000-0000-0000-000000000001', 'AAAAAAAA-0000-0000-0000-000000000001', 2000);
                -- A playlist pointing at a track that isn't in it: the pointer is cleared.
                INSERT INTO "Playlists" ("Id", "Name", "Type", "OwnerId", "CurrentMediaItemId")
                VALUES ('BBBBBBBB-0000-0000-0000-000000000002', 'Stale', 0, 'u1', 'AAAAAAAA-0000-0000-0000-000000000002');
                """);
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();

            var queue = await db.Playlists.SingleAsync(p => p.Name == "Queue");
            var stale = await db.Playlists.SingleAsync(p => p.Name == "Stale");
            Assert.Equal(Guid.Parse("CCCCCCCC-0000-0000-0000-000000000002"), queue.CurrentItemId);
            Assert.Null(stale.CurrentItemId);
        }

        // And rolling back restores the track id.
        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            var restored = await db.Database
                .SqlQueryRaw<string>("""SELECT "CurrentMediaItemId" AS "Value" FROM "Playlists" WHERE "Name" = 'Queue'""")
                .SingleAsync();
            Assert.Equal("AAAAAAAA-0000-0000-0000-000000000001", restored);
        }
    }

    private FossegrimDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FossegrimDbContext>().UseSqlite($"Data Source={_path}").Options);
}

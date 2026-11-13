using Fossegrim.Lib.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fossegrim.Api.Tests;

// UserNamesFromDisplayNames makes each display name the account's username
// (Identity's UserName, which held the email), and drops the display name.
public sealed class UserNameMigrationTests : IDisposable
{
    private const string MigrationBefore = "20261113004910_LibraryInfo";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "fossegrim-tests", $"migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task DisplayNames_BecomeUserNames_AndRollBack()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "DisplayName", "NormalizedDisplayName",
                                           "DateJoined", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES
                    -- An ordinary account: its display name becomes its username.
                    ('u1', 'kari@example.com', 'KARI@EXAMPLE.COM', 'kari@example.com', 'KARI@EXAMPLE.COM', 'Kari Nordmann', 'KARI NORDMANN', '2026-01-01', 0, 0, 0, 0, 0),
                    -- No display name: keeps the email.
                    ('u2', 'ola@example.com', 'OLA@EXAMPLE.COM', 'ola@example.com', 'OLA@EXAMPLE.COM', NULL, NULL, '2026-01-02', 0, 0, 0, 0, 0),
                    -- A display name that is the nameless account's email: keeps its own email, so nothing clashes.
                    ('u3', 'per@example.com', 'PER@EXAMPLE.COM', 'per@example.com', 'PER@EXAMPLE.COM', 'ola@example.com', 'OLA@EXAMPLE.COM', '2026-01-03', 0, 0, 0, 0, 0);
                """);
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();

            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => (u.UserName, u.NormalizedUserName, u.Email));
            Assert.Equal(("Kari Nordmann", "KARI NORDMANN", "kari@example.com"), users["u1"]);
            Assert.Equal(("ola@example.com", "OLA@EXAMPLE.COM", "ola@example.com"), users["u2"]);
            Assert.Equal(("per@example.com", "PER@EXAMPLE.COM", "per@example.com"), users["u3"]);
        }

        // Rolling back puts the names back as display names, and the emails as usernames.
        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            var restored = await db.Database
                .SqlQueryRaw<string>("""SELECT "DisplayName" || '|' || "UserName" AS "Value" FROM "AspNetUsers" WHERE "Id" = 'u1'""")
                .SingleAsync();
            Assert.Equal("Kari Nordmann|kari@example.com", restored);
        }
    }

    private FossegrimDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FossegrimDbContext>().UseSqlite($"Data Source={_path}").Options);
}

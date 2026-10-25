using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Tests;

public class DatabaseTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public DatabaseTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    // Fails when a model change was made without adding a migration (or when
    // a migration was regenerated against the wrong snapshot after a merge).
    [Fact]
    public async Task Model_HasNoChangesMissingAMigration()
    {
        await _factory.WithDbAsync(db =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Startup_AppliesEveryMigration()
    {
        await _factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fossegrim.Lib.Data;

public class FossegrimDbContextFactory : IDesignTimeDbContextFactory<FossegrimDbContext>
{
    public FossegrimDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FossegrimDbContext>();

        // Use a default connection string for migrations
        optionsBuilder.UseSqlite("Data Source=fossegrim.db");

        return new FossegrimDbContext(optionsBuilder.Options);
    }
}

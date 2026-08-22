using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Fossegrim.Lib.Models;

namespace Fossegrim.Lib.Data;

public class FossegrimDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<MediaItem> MediaItems { get; set; }
    public DbSet<Artist> Artists { get; set; }
    public DbSet<Album> Albums { get; set; }
    public DbSet<MediaFolder> MediaFolders { get; set; }

    public FossegrimDbContext(DbContextOptions<FossegrimDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Artist entity
        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Name);

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.LastModified)
                  .HasDefaultValueSql("datetime('now')");

            // Configure many-to-many relationship with MediaItems
            entity.HasMany(a => a.MediaItems)
                  .WithMany(m => m.Artists)
                  .UsingEntity(j => j.ToTable("MediaItemArtists"));

            // Configure many-to-many relationship with Albums
            entity.HasMany(a => a.Albums)
                  .WithMany(al => al.Artists)
                  .UsingEntity(j => j.ToTable("AlbumArtists"));
        });

        // Configure Album entity
        modelBuilder.Entity<Album>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Year);
            entity.HasIndex(e => e.Genre);

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.LastModified)
                  .HasDefaultValueSql("datetime('now')");

            // Configure many-to-many relationship with MediaItems
            entity.HasMany(al => al.MediaItems)
                  .WithMany(m => m.Albums)
                  .UsingEntity(j => j.ToTable("AlbumMediaItems"));
        });

        // Configure MediaItem entity
        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.FileLocation)
                  .IsUnique();

            entity.HasIndex(e => e.ArtistName);
            entity.HasIndex(e => e.Album);
            entity.HasIndex(e => e.Title);

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.LastModified)
                  .HasDefaultValueSql("datetime('now')");

            // Configure relationship with MediaFolder
            entity.HasOne(m => m.MediaFolder)
                  .WithMany(f => f.MediaItems)
                  .HasForeignKey(m => m.MediaFolderId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure MediaFolder entity
        modelBuilder.Entity<MediaFolder>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Location)
                  .IsUnique();

            entity.HasIndex(e => e.Name);

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.DateLastScanned);
        });
    }
}

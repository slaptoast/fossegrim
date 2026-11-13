using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;

namespace Fossegrim.Lib.Data;

public class FossegrimDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<MediaItem> MediaItems { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<Artist> Artists { get; set; }
    public DbSet<Album> Albums { get; set; }
    public DbSet<MediaFolder> MediaFolders { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistItem> PlaylistItems { get; set; }
    public DbSet<PlaylistShare> PlaylistShares { get; set; }
    public DbSet<PlayEvent> PlayEvents { get; set; }
    public DbSet<AppSetting> AppSettings { get; set; }
    public DbSet<UserPreferences> UserPreferences { get; set; }
    public DbSet<UserSession> UserSessions { get; set; }
    public DbSet<FavoriteTrack> FavoriteTracks { get; set; }
    public DbSet<FavoriteAlbum> FavoriteAlbums { get; set; }
    public DbSet<FavoriteArtist> FavoriteArtists { get; set; }

    public FossegrimDbContext(DbContextOptions<FossegrimDbContext> options)
        : base(options)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeUserNames();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeUserNames();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // However a user is saved (Identity, the endpoints, the seeder), the
    // name the unique index checks matches the one people see.
    private void NormalizeUserNames()
    {
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.NormalizedUserName = UserNames.Normalize(entry.Entity.UserName);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Unique display names; null (no name) doesn't count as a clash.
        modelBuilder.Entity<ApiKey>(entity =>
        {
            // Every API request looks its key up by hash.
            entity.HasIndex(e => e.Hash).IsUnique();
        });

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
            entity.HasIndex(e => e.TagVersionMismatch);

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

        // Configure Playlist entity
        modelBuilder.Entity<Playlist>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Name);

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.LastModified)
                  .HasDefaultValueSql("datetime('now')");

            // Each user may only have a single Queue playlist
            entity.HasIndex(e => new { e.OwnerId, e.Type })
                  .IsUnique()
                  .HasFilter($"\"Type\" = {(int)PlaylistType.Queue}");

            entity.HasOne(p => p.Owner)
                  .WithMany()
                  .HasForeignKey(p => p.OwnerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.CurrentItem)
                  .WithMany()
                  .HasForeignKey(p => p.CurrentItemId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure PlaylistItem entity (ordered tracks within a playlist)
        modelBuilder.Entity<PlaylistItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.PlaylistId, e.Position });

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.HasOne(pi => pi.Playlist)
                  .WithMany(p => p.Items)
                  .HasForeignKey(pi => pi.PlaylistId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(pi => pi.MediaItem)
                  .WithMany()
                  .HasForeignKey(pi => pi.MediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure PlaylistShare entity (many-to-many between Users and Playlists, with an access level)
        modelBuilder.Entity<PlaylistShare>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.PlaylistId, e.UserId })
                  .IsUnique();

            entity.Property(e => e.DateAdded)
                  .HasDefaultValueSql("datetime('now')");

            entity.HasOne(ps => ps.Playlist)
                  .WithMany(p => p.Shares)
                  .HasForeignKey(ps => ps.PlaylistId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ps => ps.User)
                  .WithMany()
                  .HasForeignKey(ps => ps.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure PlayEvent entity (per-user listening history)
        modelBuilder.Entity<PlayEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            // History is always read per user, newest first
            entity.HasIndex(e => new { e.UserId, e.PlayedAt });
            entity.HasIndex(e => e.MediaItemId);

            // The scrobble worker's query: Pending plays that are due
            entity.HasIndex(e => new { e.ScrobbleStatus, e.NextScrobbleAttemptAt });

            entity.HasOne(pe => pe.User)
                  .WithMany()
                  .HasForeignKey(pe => pe.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(pe => pe.MediaItem)
                  .WithMany()
                  .HasForeignKey(pe => pe.MediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Favourites: one row per user and item, newest-first lists per user.
        modelBuilder.Entity<FavoriteTrack>(entity =>
        {
            entity.HasKey(f => new { f.UserId, f.MediaItemId });
            entity.HasIndex(f => new { f.UserId, f.CreatedAt });
            entity.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.MediaItem).WithMany().HasForeignKey(f => f.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FavoriteAlbum>(entity =>
        {
            entity.HasKey(f => new { f.UserId, f.AlbumId });
            entity.HasIndex(f => new { f.UserId, f.CreatedAt });
            entity.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Album).WithMany().HasForeignKey(f => f.AlbumId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FavoriteArtist>(entity =>
        {
            entity.HasKey(f => new { f.UserId, f.ArtistId });
            entity.HasIndex(f => new { f.UserId, f.CreatedAt });
            entity.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Artist).WithMany().HasForeignKey(f => f.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        // Signed-in devices; deleted along with the user
        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RefreshTokenHash).IsUnique();
            entity.HasIndex(e => e.PreviousRefreshTokenHash);

            entity.HasOne(s => s.User)
                  .WithMany()
                  .HasForeignKey(s => s.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Deleted along with the user
        modelBuilder.Entity<UserPreferences>(entity =>
        {
            entity.HasOne(p => p.User)
                  .WithOne()
                  .HasForeignKey<UserPreferences>(p => p.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

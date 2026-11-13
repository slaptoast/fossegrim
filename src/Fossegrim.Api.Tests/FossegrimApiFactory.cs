using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

/// <summary>
/// Runs the real API in-process against its own throwaway SQLite database
/// (migrated on startup, exactly as in production). Shared by every test in
/// a class, so tests keep to their own users and tracks instead of assuming
/// an empty database.
/// </summary>
public class FossegrimApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-signing-key-that-is-long-enough-for-hmac-sha256";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fossegrim-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(_directory, "fossegrim.db")}");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("DataProtection:KeysDirectory", Path.Combine(_directory, "dataprotection-keys"));
        // Tests drive Scrobbler directly, with a controlled clock.
        builder.UseSetting("Scrobbling:Enabled", "false");
        // Tests run update checks on demand, against the fake ghcr.io.
        builder.UseSetting("UpdateCheck:WorkerEnabled", "false");
        // Tests run backups on demand, into this factory's own folder.
        builder.UseSetting("Backup:WorkerEnabled", "false");
        builder.UseSetting("Backup:Directory", BackupDirectory);
        // Tests request certificates on demand (HttpsTests), from a fake Let's Encrypt.
        builder.UseSetting("Https:WorkerEnabled", "false");
        builder.UseSetting("Covers:CacheDirectory", Path.Combine(_directory, "cover-cache"));
        // Every test signs in from the same address; SignInRateLimitTests sets its own.
        builder.UseSetting("RateLimiting:SignInPerMinute", SignInPerMinute.ToString());

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<LastFmClient>().ConfigurePrimaryHttpMessageHandler(() => LastFm);
            services.AddHttpClient<CoverSearchService>().ConfigurePrimaryHttpMessageHandler(() => Covers);
            services.AddHttpClient<CoverDownloader>().ConfigurePrimaryHttpMessageHandler(() => Covers);
            services.AddHttpClient<WikipediaInfoService>().ConfigurePrimaryHttpMessageHandler(() => Covers);
            services.AddHttpClient<GhcrClient>().ConfigurePrimaryHttpMessageHandler(() => Ghcr);
            services.AddSingleton<IMailTransport>(Mail);
        });
    }

    /// <summary>The sign-in rate limit; high enough by default not to get in tests' way.</summary>
    public int SignInPerMinute { get; init; } = 100_000;

    public string BackupDirectory => Path.Combine(_directory, "backups");

    /// <summary>Only used when a test factory sets Https:CertificateDirectory to it.</summary>
    public string CertificateDirectory => Path.Combine(_directory, "certs");

    /// <summary>Stands in for ws.audioscrobbler.com; every Last.fm call goes here.</summary>
    public FakeLastFmHandler LastFm { get; } = new();

    /// <summary>Stands in for the cover search providers and their image hosts.</summary>
    public FakeCoverProvidersHandler Covers { get; } = new();

    /// <summary>Stands in for ghcr.io's token and tags API.</summary>
    public FakeGhcrHandler Ghcr { get; } = new();

    /// <summary>Stands in for the SMTP server; records every email instead of sending it.</summary>
    public FakeMailTransport Mail { get; } = new();

    /// <summary>Creates a brand-new user and returns a client signed in as them.</summary>
    public async Task<HttpClient> CreateUserClientAsync() => (await CreateUserAsync()).Client;

    public Task<TestUser> CreateUserAsync() => CreateSignedInUserAsync("user", "Test User", isAdmin: false);

    /// <summary>A new user in the Admin role, signed in after the role was added so their token carries it.</summary>
    public Task<TestUser> CreateAdminAsync() => CreateSignedInUserAsync("admin", "Test Admin", isAdmin: true);

    // Created directly rather than through /api/auth/register, which is
    // closed unless an admin opens it.
    private async Task<TestUser> CreateSignedInUserAsync(string prefix, string displayName, bool isAdmin)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!x";

        string userId;
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            // Display names are unique, so each test user gets its own.
            var user = new ApplicationUser { UserName = $"{displayName} {email[..email.IndexOf('@')]}", Email = email, DateJoined = DateTime.UtcNow };
            var result = await users.CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
            if (isAdmin)
            {
                await users.AddToRoleAsync(user, "Admin");
            }
            userId = user.Id;
        }

        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return new TestUser(client, userId, email, password);
    }

    public async Task<MediaItem> AddMediaItemAsync(
        string title = "Song",
        string artist = "Band",
        string album = "Record",
        string? genre = null,
        int? year = null,
        string? linkedArtistName = null,
        string? linkedAlbumLabel = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            ArtistName = artist,
            Album = album,
            Genre = genre,
            Year = year,
            FileLocation = $"/music/{Guid.NewGuid():N}.mp3",
            Duration = TimeSpan.FromMinutes(3)
        };

        if (linkedArtistName is not null)
        {
            item.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = linkedArtistName });
        }
        if (linkedAlbumLabel is not null)
        {
            item.Albums.Add(new Album { Id = Guid.NewGuid(), Name = album, Label = linkedAlbumLabel });
        }

        await WithDbAsync(async db =>
        {
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
        });
        return item;
    }

    public async Task WithDbAsync(Func<FossegrimDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<FossegrimDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can hold the file briefly after shutdown; it's in the temp folder anyway.
            }
        }
    }
}

// Email and Password sign in as the same user again, e.g. as a second device.
public record TestUser(HttpClient Client, string UserId, string Email, string Password);

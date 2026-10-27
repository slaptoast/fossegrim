using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fossegrim-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(_directory, "fossegrim.db")}");
        builder.UseSetting("Jwt:Key", "test-signing-key-that-is-long-enough-for-hmac-sha256");
        builder.UseSetting("DataProtection:KeysDirectory", Path.Combine(_directory, "dataprotection-keys"));
        // Tests drive Scrobbler directly, with a controlled clock.
        builder.UseSetting("Scrobbling:Enabled", "false");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<LastFmClient>().ConfigurePrimaryHttpMessageHandler(() => LastFm);
            services.AddHttpClient<CoverSearchService>().ConfigurePrimaryHttpMessageHandler(() => Covers);
            services.AddHttpClient<CoverDownloader>().ConfigurePrimaryHttpMessageHandler(() => Covers);
        });
    }

    /// <summary>Stands in for ws.audioscrobbler.com; every Last.fm call goes here.</summary>
    public FakeLastFmHandler LastFm { get; } = new();

    /// <summary>Stands in for the cover search providers and their image hosts.</summary>
    public FakeCoverProvidersHandler Covers { get; } = new();

    /// <summary>Registers a brand-new user and returns a client signed in as them.</summary>
    public async Task<HttpClient> CreateUserClientAsync() => (await CreateUserAsync()).Client;

    public async Task<TestUser> CreateUserAsync()
    {
        var client = CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Test User", "Passw0rd!x"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return new TestUser(client, login.UserId);
    }

    /// <summary>A new user in the Admin role, signed in after the role was added so their token carries it.</summary>
    public async Task<TestUser> CreateAdminAsync()
    {
        var client = CreateClient();
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!x";
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Test Admin", password))).EnsureSuccessStatusCode();

        string userId;
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            await users.AddToRoleAsync(user!, "Admin");
            userId = user!.Id;
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return new TestUser(client, userId);
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

public record TestUser(HttpClient Client, string UserId);

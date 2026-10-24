using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    }

    /// <summary>Registers a brand-new user and returns a client signed in as them.</summary>
    public async Task<HttpClient> CreateUserClientAsync()
    {
        var client = CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Test User", "Passw0rd!x"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    public async Task<MediaItem> AddMediaItemAsync(string title = "Song", string artist = "Band")
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            ArtistName = artist,
            Album = "Record",
            FileLocation = $"/music/{Guid.NewGuid():N}.mp3",
            Duration = TimeSpan.FromMinutes(3)
        };

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

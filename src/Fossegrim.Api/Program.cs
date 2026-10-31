using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Fossegrim.Api.Endpoints;
using Fossegrim.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// Configure CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5085", "https://localhost:7049"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWeb", policy =>
    {
        policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

// Configure SQLite database
builder.Services.AddDbContext<FossegrimDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=fossegrim.db"));

// Configure Identity (core only - this is a token-issuing API, not cookie-based)
builder.Services.AddIdentityCore<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<FossegrimDbContext>();

// Configure JWT bearer authentication. The resolved key is written back into
// configuration so token issuing (Endpoints/Auth.cs) signs with the same key.
var jwtSection = builder.Configuration.GetSection("Jwt");
var (jwtKey, jwtKeySource) = JwtKeyStore.Resolve(jwtSection["Key"], jwtSection["KeyFile"]);
jwtSection["Key"] = jwtKey;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true
        };

        // <audio>/<img> elements can't set an Authorization header, so callers
        // pass the token via query string for the /stream and /cover endpoints.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if ((context.Request.Path.StartsWithSegments("/stream") || context.Request.Path.StartsWithSegments("/cover")) &&
                    context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Register application services
builder.Services.AddScoped<MediaLibraryService>();

// Album cover search (MusicBrainz + Cover Art Archive, Deezer) and downloads.
// The limits are shared by the whole server, as the providers count them.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CoverSearchLimits>();
builder.Services.AddHttpClient<CoverSearchService>(client => client.Timeout = TimeSpan.FromSeconds(10));
// Redirects are followed by CoverDownloader itself, checking each hop's host.
builder.Services.AddHttpClient<CoverDownloader>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Last.fm client is always registered but stays inert (IsConfigured == false)
// until an admin saves API credentials on the Settings page, or
// LastFm:ApiKey and LastFm:ApiSecret are set (which take precedence).
builder.Services.Configure<LastFmOptions>(builder.Configuration.GetSection(LastFmOptions.SectionName));
builder.Services.AddSingleton<LastFmCredentialsHolder>();
builder.Services.AddHttpClient<LastFmClient>();
builder.Services.AddScoped<LastFmAccountStore>();
builder.Services.AddScoped<LastFmSettingsStore>();
builder.Services.AddScoped<Scrobbler>();
builder.Services.AddSingleton<ScrobbleSignal>();
// Tests turn the worker off and drive Scrobbler directly.
if (builder.Configuration.GetValue("Scrobbling:Enabled", true))
{
    builder.Services.AddHostedService<ScrobbleWorker>();
}

// Update check: compares APP_VERSION with the newest release on Docker Hub.
// Development builds ("dev") have nothing to compare, so get no worker.
var appVersion = new AppVersion(builder.Configuration["APP_VERSION"] is { Length: > 0 } v ? v : "dev");
builder.Services.AddSingleton(appVersion);
builder.Services.AddSingleton<UpdateCheckState>();
builder.Services.AddHttpClient<DockerHubClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Fossegrim/{appVersion.Value}");
});
builder.Services.AddScoped<UpdateChecker>();
if (appVersion.Release is not null && builder.Configuration.GetValue("UpdateCheck:WorkerEnabled", true))
{
    builder.Services.AddHostedService<UpdateCheckWorker>();
}

builder.Services.AddScoped<SetupService>();

// Backups: zips of the database and its key material, written to
// Backup:Directory on demand or on the admin's schedule.
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddScoped<BackupService>();
if (builder.Configuration.GetValue("Backup:WorkerEnabled", true))
{
    builder.Services.AddHostedService<BackupWorker>();
}

// Data Protection encrypts stored secrets (Last.fm session keys). Its keys
// must outlive the container or those secrets become unreadable, so Docker
// points DataProtection:KeysDirectory at the /data volume. Unset, the
// framework default (per-user profile folder) is used, which is fine for dev.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Fossegrim");
var dataProtectionKeysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysDirectory))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysDirectory));
}

var app = builder.Build();

switch (jwtKeySource)
{
    case JwtKeySource.KeyFileCreated:
        app.Logger.LogInformation("Generated new JWT signing key at {KeyFile}", jwtSection["KeyFile"]);
        break;
    case JwtKeySource.KeyFileLoaded:
        app.Logger.LogInformation("Loaded JWT signing key from {KeyFile}", jwtSection["KeyFile"]);
        break;
    case JwtKeySource.Ephemeral:
        app.Logger.LogWarning("No Jwt:Key or Jwt:KeyFile configured; using a temporary signing key. All sessions end when the API restarts.");
        break;
}

// Apply migrations, then seed roles and admin user
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<FossegrimDbContext>().Database.MigrateAsync();
    await RoleSeeder.SeedRolesAndAdminAsync(scope.ServiceProvider, seedDefaultAdmin: app.Environment.IsDevelopment());
    await scope.ServiceProvider.GetRequiredService<SetupService>().MarkExistingInstallCompleteAsync();
    await scope.ServiceProvider.GetRequiredService<LastFmSettingsStore>().LoadAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Serve the Blazor WebAssembly app (Fossegrim.Web) from this process's
// wwwroot -- populated at publish time via the ProjectReference below.
// Same-origin, so the browser never needs CORS for its own requests here;
// AllowWeb below only matters when Web is hosted separately (local dev).
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Enable CORS
app.UseCors("AllowWeb");

app.UseAuthentication();
app.UseAuthorization();

// Map all endpoints
app.MapAuthEndpoints();
app.MapAlbumEndpoints();
app.MapArtistEndpoints();
app.MapMediaItemEndpoints();
app.MapLibraryEndpoints();
app.MapPlaylistEndpoints();
app.MapPlayEndpoints();
app.MapAdminEndpoints();
app.MapStreamingEndpoints();
app.MapCoverEndpoints();
app.MapVersionEndpoints();
app.MapLastFmEndpoints();
app.MapUserEndpoints();
app.MapPreferenceEndpoints();
app.MapAlbumCoverEndpoints();
app.MapBackupEndpoints();
app.MapSetupEndpoints();

app.MapFallbackToFile("index.html");

app.Run();

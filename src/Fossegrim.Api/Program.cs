using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Fossegrim.Lib.Services.Library;
using Microsoft.AspNetCore.Authentication;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Fossegrim.Api.Endpoints;
using Fossegrim.Api.Services;
using Fossegrim.Api.Services.Https;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// Configure CORS
// Only needed when the web app is served from another origin (local dev, see
// appsettings.Development.json); in production it's served from here.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
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
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Too many wrong passwords lock the account for a while (see Endpoints/Auth.cs).
        options.Lockout.MaxFailedAccessAttempts = SignInLimits.MaxFailedAttempts;
        options.Lockout.DefaultLockoutTimeSpan = SignInLimits.LockoutDuration;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<FossegrimDbContext>()
    // Password reset links (Services/PasswordResets.cs).
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = PasswordResets.LinkLifetime);

// Configure JWT bearer authentication. The resolved key is written back into
// configuration so token issuing (Services/SessionService.cs) signs with the same key.
var jwtSection = builder.Configuration.GetSection("Jwt");
var (jwtKey, jwtKeySource) = JwtKeyStore.Resolve(jwtSection["Key"], jwtSection["KeyFile"]);
jwtSection["Key"] = jwtKey;
TokenValidationParameters JwtValidation(string audience) => new()
{
    ValidateIssuer = true,
    ValidIssuer = jwtSection["Issuer"],
    ValidateAudience = true,
    ValidAudience = audience,
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
    ValidateLifetime = true
};
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    // Access tokens, in the Authorization header only.
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = JwtValidation(jwtSection["Audience"]!);
        options.Events = new JwtBearerEvents { OnTokenValidated = MediaAuth.RequireActiveSessionAsync };
    })
    // Media tokens, for /stream and /cover: <audio>/<img> elements can't set
    // an Authorization header, so the token goes in the URL. A media token
    // has its own audience, so it can't be used on the rest of the API, and
    // an access token in a URL isn't accepted.
    .AddJwtBearer(MediaAuth.Scheme, options =>
    {
        options.TokenValidationParameters = JwtValidation(JwtTokenService.MediaAudience(jwtSection["Audience"]!));
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }
                else
                {
                    context.NoResult();
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = MediaAuth.RequireActiveSessionAsync
        };
    })
    // Other applications (e.g. Lidarr) start scans with an API key; see Endpoints/Scans.cs.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.Scheme, null);

builder.Services.AddAuthorization();

// Per client address: signing in, registering, claiming a fresh install and
// changing a password. Account lockout (above) covers guessing spread
// across addresses. Behind a reverse proxy every client shares the proxy's
// address, so this is a server-wide limit there.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(SignInLimits.Policy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:SignInPerMinute", SignInLimits.DefaultPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

// Register application services
builder.Services.AddScoped<MediaLibraryService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<CoverCache>();

// Library scans run in the background, one at a time (see Endpoints/Scans.cs);
// renames share the lock so they never overlap a scan.
builder.Services.AddSingleton<LibraryLock>();
builder.Services.AddSingleton<ScanQueue>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<SearchIndex>();
builder.Services.AddScoped<LibraryBrowser>();
builder.Services.AddScoped<ScanRequests>();
builder.Services.AddScoped<FileRenamer>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddHostedService<ScanWorker>();

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

// Update check: compares APP_VERSION with the newest release on ghcr.io.
// Development builds ("dev") have nothing to compare, so get no worker.
var appVersion = new AppVersion(builder.Configuration["APP_VERSION"] is { Length: > 0 } v ? v : "dev");
builder.Services.AddSingleton(appVersion);
builder.Services.AddSingleton<UpdateCheckState>();
builder.Services.AddHttpClient<GhcrClient>(client =>
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

// Email (optional, set up on the Settings page): "Forgot password?" links.
builder.Services.AddScoped<EmailSettingsStore>();
builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
builder.Services.AddSingleton<EmailQueue>();
builder.Services.AddHostedService<EmailWorker>();
builder.Services.AddSingleton<ResetEmailThrottle>();
builder.Services.AddScoped<PasswordResets>();
builder.Services.AddScoped<AccountDeletion>();

// Backups: zips of the database and its key material, written to
// Backup:Directory on demand or on the admin's schedule.
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddScoped<BackupService>();
if (builder.Configuration.GetValue("Backup:WorkerEnabled", true))
{
    builder.Services.AddHostedService<BackupWorker>();
}

// HTTPS (optional): Let's Encrypt certificates written to the folder the
// bundled proxy serves them from (Https:CertificateDirectory, /certs in
// Docker Compose). Without that folder there's nothing to manage.
builder.Services.AddSingleton<CertificateFiles>();
builder.Services.AddSingleton<CertificateCoordinator>();
builder.Services.AddSingleton<AcmeChallenges>();
builder.Services.AddSingleton<IAcmeIssuer, LetsEncryptIssuer>();
builder.Services.AddScoped<CertificateService>();
if (builder.Configuration.GetValue("Https:WorkerEnabled", true))
{
    builder.Services.AddHostedService<CertificateWorker>();
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

// `fossegrim reset-password <email>` (see DOCKER.md): prints a reset link
// for an admin who can't sign in, then exits without starting the server.
if (args is ["reset-password", ..])
{
    return await ResetPasswordCommand.RunAsync(app, args[1..]);
}

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
    var startupDb = scope.ServiceProvider.GetRequiredService<FossegrimDbContext>();
    await startupDb.Database.MigrateAsync();
    // Renames any accounts sharing a display name from before names were unique.
    await DisplayNames.EnsureUniqueAsync(startupDb, app.Logger);
    // Fills the search indexes on first run, and if they've got out of step.
    await scope.ServiceProvider.GetRequiredService<SearchIndex>().EnsureBuiltAsync();
    await RoleSeeder.SeedRolesAndAdminAsync(scope.ServiceProvider, seedDefaultAdmin: app.Environment.IsDevelopment());
    await scope.ServiceProvider.GetRequiredService<SetupService>().MarkExistingInstallCompleteAsync();
    await scope.ServiceProvider.GetRequiredService<LastFmSettingsStore>().LoadAsync();
    await scope.ServiceProvider.GetRequiredService<CertificateService>().SyncRedirectAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No HSTS: behind the proxy, HTTPS can be turned off again from the
// Settings page, and HSTS would leave browsers refusing plain HTTP.
app.UseHttpsRedirection();
app.UseSecurityHeaders();

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
app.UseRateLimiter();

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
app.MapScanEndpoints();
app.MapApiKeyEndpoints();
app.MapLibraryFileEndpoints();
app.MapRandomEndpoints();
app.MapSearchEndpoints();
app.MapFavoriteEndpoints();
app.MapPasswordResetEndpoints();
app.MapHttpsEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
return 0;

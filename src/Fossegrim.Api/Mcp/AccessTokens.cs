using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fossegrim.Api.Mcp;

/// <summary>
/// Each user's personal access tokens for the MCP endpoint. A token is shown
/// once, when made; only its SHA-256 hash is stored (tokens are long and
/// random, so a plain hash is enough).
/// </summary>
public class AccessTokenService
{
    // Distinct from API keys ("fsg_"), so the bearer handler below can leave
    // other Authorization headers (the web app's JWTs) alone.
    public const string TokenPrefix = "fsgp_";
    public const int MaxNameLength = 100;

    private readonly FossegrimDbContext _db;
    private readonly TimeProvider _time;

    public AccessTokenService(FossegrimDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public Task<List<AccessToken>> ListAsync(string userId) =>
        _db.AccessTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => EF.Functions.Collate(t.Name, "NOCASE"))
            .ToListAsync();

    public async Task<(AccessToken Token, string Secret)> CreateAsync(string userId, string name)
    {
        var secret = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new AccessToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name.Trim(),
            Prefix = secret[..12],
            Hash = Hash(secret),
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        _db.AccessTokens.Add(token);
        await _db.SaveChangesAsync();
        return (token, secret);
    }

    /// <summary>Revokes one of the user's tokens; false if they have no such token.</summary>
    public async Task<bool> DeleteAsync(string userId, Guid id) =>
        await _db.AccessTokens.Where(t => t.UserId == userId && t.Id == id).ExecuteDeleteAsync() > 0;

    /// <summary>Revokes all the user's tokens (a password reset does this).</summary>
    public Task<int> DeleteAllAsync(string userId) =>
        _db.AccessTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();

    public static string? NameError(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the token a name, such as the app you'll use it in."
        : name.Trim().Length > MaxNameLength ? $"Token names can be at most {MaxNameLength} characters."
        : null;

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Authenticates "Authorization: Bearer fsgp_…" with a personal access token,
/// as the user who made it. Only /mcp asks for this scheme, so a token can't
/// be used on the rest of the API. Deleted accounts' tokens stop working.
/// </summary>
public class AccessTokenAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public new const string Scheme = "AccessToken";

    // Recording every use would write to the database on each request.
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly FossegrimDbContext _db;
    private readonly TimeProvider _time;

    public AccessTokenAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        FossegrimDbContext db,
        TimeProvider time)
        : base(options, logger, encoder)
    {
        _db = db;
        _time = time;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var secret = header[bearer.Length..].Trim();
        if (!secret.StartsWith(AccessTokenService.TokenPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = AccessTokenService.Hash(secret);
        var token = await _db.AccessTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.Hash == hash);
        if (token?.User is not { DeletedAt: null } user)
        {
            return AuthenticateResult.Fail("Invalid access token.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            token.LastUsedAt = now;
            await _db.SaveChangesAsync();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName ?? "")],
            Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

/// <summary>Whether an admin has turned the MCP endpoint on. Read on every /mcp request, so kept in memory.</summary>
public class McpSwitch
{
    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}

/// <summary>The admin's MCP on/off setting, saved in AppSettings ("Mcp:Enabled"). Off until turned on.</summary>
public class McpSettingsStore
{
    private const string Setting = "Mcp:Enabled";

    private readonly FossegrimDbContext _db;
    private readonly McpSwitch _switch;
    private readonly TimeProvider _time;

    public McpSettingsStore(FossegrimDbContext db, McpSwitch mcpSwitch, TimeProvider time)
    {
        _db = db;
        _switch = mcpSwitch;
        _time = time;
    }

    /// <summary>At startup: reads the saved setting into <see cref="McpSwitch"/>.</summary>
    public async Task LoadAsync()
    {
        var value = await _db.AppSettings.AsNoTracking().Where(s => s.Key == Setting).Select(s => s.Value).FirstOrDefaultAsync();
        _switch.Enabled = bool.TryParse(value, out var enabled) && enabled;
    }

    public async Task SaveAsync(bool enabled)
    {
        var value = enabled ? "true" : "false";
        var setting = await _db.AppSettings.FindAsync(Setting);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = Setting, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        }
        await _db.SaveChangesAsync();
        _switch.Enabled = enabled;
    }
}

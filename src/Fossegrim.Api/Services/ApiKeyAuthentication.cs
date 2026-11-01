using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fossegrim.Api.Services;

/// <summary>
/// Creates, lists and revokes API keys. A key is shown once, when created;
/// only its SHA-256 hash is stored (keys are long and random, so a plain
/// hash is enough; there's nothing to brute-force).
/// </summary>
public class ApiKeyService
{
    private const string KeyPrefix = "fsg_";

    private readonly FossegrimDbContext _db;
    private readonly TimeProvider _time;

    public ApiKeyService(FossegrimDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public Task<List<ApiKey>> ListAsync() => _db.ApiKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync();

    public async Task<(ApiKey Key, string Secret)> CreateAsync(string name)
    {
        var secret = KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Prefix = secret[..12],
            Hash = Hash(secret),
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync();
        return (key, secret);
    }

    public async Task<bool> DeleteAsync(Guid id) => await _db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync() > 0;

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Authenticates requests carrying an X-Api-Key header. The resulting
/// identity has the key's name and no roles, so it can only reach endpoints
/// that ask for this scheme (the external scan endpoints).
/// </summary>
public class ApiKeyAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    // Recording every use would write to the database on each request.
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly FossegrimDbContext _db;
    private readonly TimeProvider _time;

    public ApiKeyAuthentication(
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
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyService.Hash(values.ToString().Trim());
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash);
        if (key is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (key.LastUsedAt is null || now - key.LastUsedAt > LastUsedResolution)
        {
            key.LastUsedAt = now;
            await _db.SaveChangesAsync();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, key.Name), new Claim(ClaimTypes.NameIdentifier, key.Id.ToString())],
            Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}

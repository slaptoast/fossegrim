using System.Security.Claims;
using System.Security.Cryptography;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Signed-in devices. Signing in creates a <see cref="UserSession"/> and
/// returns an access token (a JWT carrying the session id) plus a refresh
/// token. Every request checks that its session still exists, so signing a
/// device out takes effect at once rather than when its token expires.
/// </summary>
public class SessionService
{
    // Recording every request would write to the database each time.
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    private const int DefaultMediaExpiryMinutes = 7 * 24 * 60;

    private readonly FossegrimDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;

    public SessionService(FossegrimDbContext db, UserManager<ApplicationUser> users, IConfiguration configuration, TimeProvider time)
    {
        _db = db;
        _users = users;
        _configuration = configuration;
        _time = time;
    }

    public async Task<LoginResponse> SignInAsync(ApplicationUser user, string? clientName, string? userAgent)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var refreshToken = NewRefreshToken();
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ClientName = Truncate(clientName, 100),
            UserAgent = Truncate(userAgent, 512),
            CreatedAt = now,
            LastUsedAt = now,
            RefreshTokenHash = ApiKeyService.Hash(refreshToken),
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
        return await IssueAsync(user, session.Id, refreshToken);
    }

    /// <summary>
    /// New tokens for a refresh token, or null when it isn't (or is no
    /// longer) valid. The refresh token is replaced each time. The one it
    /// replaced keeps working until the new one is used, in case the app
    /// never received it; presenting that old one again replaces the token
    /// it led to, so only one copy of a session can keep refreshing.
    /// </summary>
    public async Task<LoginResponse?> RefreshAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var hash = ApiKeyService.Hash(refreshToken.Trim());
        var session = await _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash || s.PreviousRefreshTokenHash == hash);
        if (session?.User is null)
        {
            return null;
        }

        var next = NewRefreshToken();
        if (session.RefreshTokenHash == hash)
        {
            session.PreviousRefreshTokenHash = hash;
        }
        session.RefreshTokenHash = ApiKeyService.Hash(next);
        session.LastUsedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        return await IssueAsync(session.User, session.Id, next);
    }

    /// <summary>Whether the session is still signed in; also records when it was last used.</summary>
    public async Task<bool> IsActiveAsync(Guid sessionId)
    {
        var lastUsedAt = await _db.UserSessions
            .Where(s => s.Id == sessionId)
            .Select(s => (DateTime?)s.LastUsedAt)
            .FirstOrDefaultAsync();
        if (lastUsedAt is null)
        {
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (now - lastUsedAt > LastUsedResolution)
        {
            await _db.UserSessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now));
        }
        return true;
    }

    public async Task<IReadOnlyList<SessionDto>> ListAsync(string userId, Guid? currentSessionId)
    {
        var sessions = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync();
        return sessions
            .OrderByDescending(s => s.Id == currentSessionId)
            .ThenByDescending(s => s.LastUsedAt)
            .Select(s => new SessionDto(s.Id, s.ClientName, UserAgents.Describe(s.UserAgent), s.CreatedAt, s.LastUsedAt, s.Id == currentSessionId))
            .ToList();
    }

    /// <summary>Signs one of the user's sessions out; false if they have no such session.</summary>
    public async Task<bool> RevokeAsync(string userId, Guid sessionId) =>
        await _db.UserSessions.Where(s => s.UserId == userId && s.Id == sessionId).ExecuteDeleteAsync() > 0;

    /// <summary>Signs out every session of the user's except <paramref name="keep"/> (all of them when null).</summary>
    public Task<int> RevokeOthersAsync(string userId, Guid? keep)
    {
        var sessions = _db.UserSessions.Where(s => s.UserId == userId);
        // Not "s.Id != keep": with a null keep, SQL's "<> NULL" matches nothing.
        if (keep is { } kept)
        {
            sessions = sessions.Where(s => s.Id != kept);
        }
        return sessions.ExecuteDeleteAsync();
    }

    /// <summary>The session the request's token belongs to.</summary>
    public static Guid? CurrentSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtTokenService.SessionClaim), out var id) ? id : null;

    private async Task<LoginResponse> IssueAsync(ApplicationUser user, Guid sessionId, string refreshToken)
    {
        // Read on every refresh, so role changes reach signed-in apps.
        var roles = await _users.GetRolesAsync(user);
        var jwt = _configuration.GetSection("Jwt");
        var (token, expiresAt) = JwtTokenService.GenerateToken(
            user,
            roles,
            jwt["Issuer"]!,
            jwt["Audience"]!,
            jwt["Key"]!,
            jwt.GetValue<int>("ExpiryMinutes"),
            sessionId);

        var (mediaToken, mediaExpiresAt) = JwtTokenService.GenerateMediaToken(
            user.Id,
            jwt["Issuer"]!,
            jwt["Audience"]!,
            jwt["Key"]!,
            jwt.GetValue("MediaExpiryMinutes", DefaultMediaExpiryMinutes),
            sessionId);

        return new LoginResponse(token, expiresAt, user.Id, user.UserName!, user.Email!, user.DisplayName, roles.ToList(), refreshToken, sessionId, mediaToken, mediaExpiresAt);
    }

    private static string NewRefreshToken() =>
        "fsr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int length)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}

namespace Fossegrim.Contracts.Dtos;

// ClientName is how the app is listed under the user's signed-in devices,
// e.g. "Fossegrim for iOS". Without one, the list describes the user agent.
public record LoginRequest(string Email, string Password, string? ClientName = null);

// Token is the access token (a JWT, valid until ExpiresAt). Swap
// RefreshToken for new tokens at /api/auth/refresh; it doesn't expire, but
// each refresh returns a new one to use next time.
public record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    string UserId,
    string UserName,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    string? RefreshToken = null,
    Guid? SessionId = null);

public record RefreshRequest(string RefreshToken);

// A signed-in app or browser. Device describes its user agent, when it sent a recognisable one.
public record SessionDto(Guid Id, string? ClientName, string? Device, DateTime SignedInAt, DateTime LastUsedAt, bool IsCurrent);

public record RegisterRequest(string Email, string DisplayName, string Password, string? ClientName = null);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

public record ProfileDto(string UserId, string UserName, string Email, string? DisplayName, string? PhoneNumber);

public record UpdateProfileRequest(string? DisplayName, string? PhoneNumber);

public record IdentityErrorsResponse(IReadOnlyList<string> Errors);

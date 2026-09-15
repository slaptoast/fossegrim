namespace Fossegrim.Contracts.Dtos;

public record LoginRequest(string Email, string Password);

public record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    string UserId,
    string UserName,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles);

public record RegisterRequest(string Email, string DisplayName, string Password);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

public record ProfileDto(string UserId, string UserName, string Email, string? DisplayName, string? PhoneNumber);

public record UpdateProfileRequest(string? DisplayName, string? PhoneNumber);

public record IdentityErrorsResponse(IReadOnlyList<string> Errors);

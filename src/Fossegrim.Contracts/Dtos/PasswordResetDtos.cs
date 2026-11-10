namespace Fossegrim.Contracts.Dtos;

// Public, so the "Forgot password?" page knows whether to offer an email
// link or to tell the person to ask an admin.
public record PasswordResetOptionsDto(bool ByEmail);

public record ForgotPasswordRequest(string Email);

// UserId and Token come from the reset link.
public record ResetPasswordRequest(string UserId, string Token, string NewPassword);

// Path is relative to the web app, e.g. "/reset-password?user=…&token=…";
// the admin's browser puts its own address in front.
public record PasswordResetLinkDto(string Path, DateTime ExpiresAt);

public record AdminUserDto(string UserId, string Email, string DisplayName, bool IsAdmin, bool LockedOut);

// The outgoing mail server. Security is one of SmtpSecurity. The password is
// never sent back out; HasPassword says whether one is saved. PublicUrl is
// the address people reach the server on, used in emailed links.
public record EmailSettingsDto(
    bool Configured,
    string? Host,
    int Port,
    string Security,
    string? UserName,
    bool HasPassword,
    string? FromAddress,
    string? FromName,
    string? PublicUrl);

// Password null keeps the saved one; "" clears it.
public record SaveEmailSettingsRequest(
    string Host,
    int Port,
    string Security,
    string? UserName,
    string? Password,
    string FromAddress,
    string? FromName,
    string PublicUrl);

public record TestEmailRequest(string To);

public static class SmtpSecurity
{
    // STARTTLS, required (usually port 587); TLS from the start (465); or none (25, local relays).
    public const string StartTls = "StartTls";
    public const string SslOnConnect = "SslOnConnect";
    public const string None = "None";

    public static readonly IReadOnlyList<string> All = [StartTls, SslOnConnect, None];
}

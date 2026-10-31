namespace Fossegrim.Contracts.Dtos;

// Public, so the sign-in page can send a fresh install to the wizard and
// hide "Create account" when registration is closed. SetupRequired: there
// are no users yet, so the wizard's first step (creating the admin) is open
// to anyone. SetupComplete: the wizard was finished (or this install
// predates it); until then an admin is sent back to it after signing in.
public record SetupStatusDto(bool SetupRequired, bool SetupComplete, bool RegistrationOpen);

public record CreateFirstAdminRequest(string Email, string DisplayName, string Password);

public record RegistrationSettingsRequest(bool Open);

// An account created by an admin (e.g. while registration is closed).
public record CreateUserRequest(string Email, string DisplayName, string Password, bool IsAdmin);

public record CreatedUserDto(string UserId, string Email, string DisplayName, bool IsAdmin);

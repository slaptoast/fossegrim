namespace Fossegrim.Contracts.Dtos;

// What other users can see about someone: never their email.
public record UserSummaryDto(string UserId, string DisplayName);

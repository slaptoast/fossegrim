namespace Fossegrim.Contracts.Dtos;

// Prefix: the key's first characters, to tell keys apart; the rest is never shown again.
public record ApiKeyDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

public record CreateApiKeyRequest(string Name);

// Secret is the whole key, returned only this once.
public record CreatedApiKeyDto(ApiKeyDto Key, string Secret);

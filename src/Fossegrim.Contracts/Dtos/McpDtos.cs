namespace Fossegrim.Contracts.Dtos;

// The MCP endpoint (/mcp): AI assistants reach the caller's library and
// playlists with a personal access token. An admin turns it on or off.

public record McpSettingsDto(bool Enabled);

// A personal access token (Prefix is its first characters, to tell tokens apart).
public record AccessTokenDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

// The caller's tokens, and whether an admin has turned MCP on (tokens can only be made then).
public record AccessTokenListDto(bool McpEnabled, IReadOnlyList<AccessTokenDto> Tokens);

public record CreateAccessTokenRequest(string Name);

// Secret is the token itself: shown once, never again.
public record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);

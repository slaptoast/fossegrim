using System.Security.Claims;
using Fossegrim.Api.Mcp;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// The MCP endpoint's switch (admins) and each user's personal access tokens
/// for it. Tokens can only be made while MCP is on; turning it off keeps
/// them, and /mcp answers 404 until it's on again.
/// </summary>
public static class McpSettings
{
    public static void MapMcpSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/mcp")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        admin.MapGet("/", (McpSwitch mcp) => Results.Ok(new McpSettingsDto(mcp.Enabled)))
            .WithName("GetMcpSettings");

        admin.MapPut("/", async (McpSettingsDto request, McpSettingsStore store) =>
        {
            await store.SaveAsync(request.Enabled);
            return Results.Ok(new McpSettingsDto(request.Enabled));
        })
        .WithName("SaveMcpSettings");

        var tokens = app.MapGroup("/api/access-tokens")
            .WithTags("Access tokens")
            .RequireAuthorization();

        tokens.MapGet("/", async (ClaimsPrincipal principal, AccessTokenService service, McpSwitch mcp) =>
            Browse.UserId(principal) is not { } userId
                ? Results.Unauthorized()
                : Results.Ok(new AccessTokenListDto(mcp.Enabled, (await service.ListAsync(userId)).Select(ToDto).ToList())))
        .WithName("GetAccessTokens");

        tokens.MapPost("/", async (CreateAccessTokenRequest request, ClaimsPrincipal principal, AccessTokenService service, McpSwitch mcp) =>
        {
            if (Browse.UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }
            if (!mcp.Enabled)
            {
                return Results.BadRequest(new ApiErrorResponse("An admin has to turn on MCP in Settings first."));
            }
            if (AccessTokenService.NameError(request.Name) is { } error)
            {
                return Results.BadRequest(new ApiErrorResponse(error));
            }

            var (token, secret) = await service.CreateAsync(userId, request.Name);
            return Results.Ok(new CreatedAccessTokenDto(ToDto(token), secret));
        })
        .WithName("CreateAccessToken");

        tokens.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, AccessTokenService service) =>
            Browse.UserId(principal) is not { } userId ? Results.Unauthorized()
            : await service.DeleteAsync(userId, id) ? Results.NoContent()
            : Results.NotFound())
        .WithName("RevokeAccessToken");
    }

    private static AccessTokenDto ToDto(AccessToken t) => new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt);
}

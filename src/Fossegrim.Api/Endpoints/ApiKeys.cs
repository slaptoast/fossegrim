using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;

namespace Fossegrim.Api.Endpoints;

/// <summary>Admin: API keys other applications use to start library scans.</summary>
public static class ApiKeys
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/api-keys")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        group.MapGet("/", async (ApiKeyService keys) => Results.Ok((await keys.ListAsync()).Select(ToDto)))
            .WithName("GetApiKeys")
            .WithOpenApi();

        group.MapPost("/", async (CreateApiKeyRequest request, ApiKeyService keys) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
                {
                    return Results.BadRequest(new ApiErrorResponse("Give the key a name of up to 100 characters."));
                }

                var (key, secret) = await keys.CreateAsync(request.Name);
                return Results.Created($"/api/admin/api-keys/{key.Id}", new CreatedApiKeyDto(ToDto(key), secret));
            })
            .WithName("CreateApiKey")
            .WithOpenApi();

        group.MapDelete("/{id:guid}", async (Guid id, ApiKeyService keys) =>
                await keys.DeleteAsync(id) ? Results.NoContent() : Results.NotFound())
            .WithName("RevokeApiKey")
            .WithOpenApi();
    }

    private static ApiKeyDto ToDto(ApiKey key) => new(key.Id, key.Name, key.Prefix, key.CreatedAt, key.LastUsedAt);
}

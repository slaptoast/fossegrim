using System.Security.Claims;
using System.Text.Json;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// The signed-in user's appearance settings as one JSON document (see
/// UserPreferences). The web client owns its shape; the server just stores
/// it, so new settings don't need API changes.
/// </summary>
public static class Preferences
{
    // Plenty for themes and layouts; stops the table being used as file storage.
    public const int MaxBytes = 256 * 1024;

    public static void MapPreferenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/me/preferences")
            .WithTags("Preferences")
            .RequireAuthorization();

        // An empty object when nothing has been saved yet.
        group.MapGet("/", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var stored = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Id);
            return Results.Content(stored?.Json ?? "{}", "application/json");
        })
        .WithName("GetPreferences")
        .WithOpenApi();

        // Replaces the whole document.
        group.MapPut("/", async (
            HttpRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (request.ContentLength > MaxBytes)
            {
                return Results.Problem($"Preferences can be at most {MaxBytes / 1024} KB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > MaxBytes)
            {
                return Results.Problem($"Preferences can be at most {MaxBytes / 1024} KB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Results.BadRequest("Preferences must be a JSON object.");
                }
            }
            catch (JsonException)
            {
                return Results.BadRequest("Preferences must be valid JSON.");
            }

            var stored = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == user.Id);
            if (stored is null)
            {
                db.UserPreferences.Add(new UserPreferences { UserId = user.Id, Json = body });
            }
            else
            {
                stored.Json = body;
                stored.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .Accepts<JsonElement>("application/json")
        .WithName("SavePreferences")
        .WithOpenApi();
    }
}

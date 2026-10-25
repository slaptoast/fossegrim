using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Users
{
    private const int MinQueryLength = 2;
    private const int MaxResults = 10;
    private const string LikeEscape = "\\";

    public static void MapUserEndpoints(this WebApplication app)
    {
        var userGroup = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization();

        // Finds people to share a playlist with: display names containing q,
        // or the one account whose email is exactly q. Only display names are
        // returned, so a search can't be used to collect email addresses.
        userGroup.MapGet("/search", async (
            string? q,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var query = q?.Trim() ?? "";
            if (query.Length < MinQueryLength)
            {
                return Results.Ok(Array.Empty<UserSummaryDto>());
            }

            var pattern = $"%{EscapeLike(query)}%";
            var normalizedEmail = userManager.NormalizeEmail(query);
            var matches = await db.Users
                .Where(u => u.Id != user.Id)
                .Where(u => EF.Functions.Like(u.DisplayName, pattern, LikeEscape) || u.NormalizedEmail == normalizedEmail)
                .OrderBy(u => u.DisplayName)
                .Take(MaxResults)
                .Select(u => new UserSummaryDto(u.Id, u.DisplayName ?? "Unnamed user"))
                .ToListAsync();

            return Results.Ok(matches);
        })
        .WithName("SearchUsers")
        .WithOpenApi();
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

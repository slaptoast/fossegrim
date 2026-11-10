using System.Security.Claims;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// An admin deleting or restoring an account. Deleting only marks the
/// account (<see cref="ApplicationUser.DeletedAt"/>): its playlists, plays,
/// favourites and settings stay, so restoring it brings everything back.
/// A deleted account is signed out everywhere, can't sign in, refresh or
/// use a reset link, and doesn't appear in account lists or user search.
/// </summary>
public class AccountDeletion
{
    public enum Result { Done, NotFound, Self, LastAdmin }

    private readonly FossegrimDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly SessionService _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountDeletion> _logger;

    public AccountDeletion(FossegrimDbContext db, UserManager<ApplicationUser> users, SessionService sessions, TimeProvider time, ILogger<AccountDeletion> logger)
    {
        _db = db;
        _users = users;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    public static async Task<List<string>> AdminIdsAsync(FossegrimDbContext db)
    {
        var adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstOrDefaultAsync();
        return await db.UserRoles.Where(ur => ur.RoleId == adminRoleId).Select(ur => ur.UserId).ToListAsync();
    }

    public async Task<Result> DeleteAsync(string userId, ClaimsPrincipal deletedBy)
    {
        if (_users.GetUserId(deletedBy) == userId)
        {
            return Result.Self;
        }

        // Mark first, then check an admin is left, in one transaction:
        // SQLite runs one write at a time, so two admins deleting each
        // other at once can't both see the other still there.
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var marked = await _db.Users
                .Where(u => u.Id == userId && u.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, now));
            if (marked == 0)
            {
                return Result.NotFound;
            }

            var adminIds = await AdminIdsAsync(_db);
            if (!await _db.Users.AnyAsync(u => adminIds.Contains(u.Id) && u.DeletedAt == null))
            {
                await transaction.RollbackAsync();
                return Result.LastAdmin;
            }
            await transaction.CommitAsync();
        }

        await _sessions.RevokeOthersAsync(userId, keep: null);
        // Reset links are tied to the security stamp, so this voids any out there.
        if (await _users.FindByIdAsync(userId) is { } user)
        {
            await _users.UpdateSecurityStampAsync(user);
        }
        _logger.LogInformation("User {UserId} was deleted by {AdminId}", userId, _users.GetUserId(deletedBy));
        return Result.Done;
    }

    /// <summary>False when there's no deleted account with that id.</summary>
    public async Task<bool> RestoreAsync(string userId)
    {
        var restored = await _db.Users
            .Where(u => u.Id == userId && u.DeletedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, (DateTime?)null));
        if (restored > 0)
        {
            _logger.LogInformation("User {UserId} was restored", userId);
        }
        return restored > 0;
    }
}

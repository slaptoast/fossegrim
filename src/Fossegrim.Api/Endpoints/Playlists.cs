using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Playlists
{
    public static void MapPlaylistEndpoints(this WebApplication app)
    {
        var playlistGroup = app.MapGroup("/api/playlists")
            .WithTags("Playlists")
            .RequireAuthorization();

        playlistGroup.MapGet("/", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlists = await db.Playlists
                .Include(p => p.Owner)
                .Include(p => p.Shares)
                .Include(p => p.Items)
                .Where(p => p.OwnerId == user.Id || p.Shares.Any(s => s.UserId == user.Id))
                .ToListAsync();

            return Results.Ok(playlists.Select(p => ToPlaylistDto(p, user.Id)));
        })
        .WithName("GetAllPlaylists")
        .WithOpenApi();

        playlistGroup.MapGet("/queue", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var queue = await GetOrCreateQueueAsync(db, user.Id);
            return Results.Ok(ToPlaylistDetailDto(queue, user.Id));
        })
        .WithName("GetQueue")
        .WithOpenApi();

        playlistGroup.MapPut("/queue", async (
            SetQueueRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var mediaItemIds = request.MediaItemIds.ToList();

            if (mediaItemIds.Count > 0)
            {
                var existingMediaItemIds = await db.MediaItems
                    .Where(m => mediaItemIds.Contains(m.Id))
                    .Select(m => m.Id)
                    .ToListAsync();

                var missingIds = mediaItemIds.Except(existingMediaItemIds).ToList();
                if (missingIds.Count > 0)
                {
                    return Results.BadRequest(new ApiErrorResponse($"Unknown media item id(s): {string.Join(", ", missingIds)}"));
                }
            }

            if (request.CurrentMediaItemId is not null && !mediaItemIds.Contains(request.CurrentMediaItemId.Value))
            {
                return Results.BadRequest(new ApiErrorResponse("CurrentMediaItemId must be one of the provided MediaItemIds."));
            }

            var queue = await GetOrCreateQueueAsync(db, user.Id);

            db.PlaylistItems.RemoveRange(queue.Items);

            var position = 0.0;
            foreach (var mediaItemId in mediaItemIds)
            {
                db.PlaylistItems.Add(new PlaylistItem
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = queue.Id,
                    MediaItemId = mediaItemId,
                    Position = position,
                    DateAdded = DateTime.UtcNow
                });
                position += PositionGap;
            }

            queue.CurrentMediaItemId = request.CurrentMediaItemId;
            queue.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var updated = await GetOrCreateQueueAsync(db, user.Id);
            return Results.Ok(ToPlaylistDetailDto(updated, user.Id));
        })
        .WithName("SetQueue")
        .WithOpenApi();

        playlistGroup.MapPatch("/queue/current", async (
            SetQueueCurrentTrackRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var queue = await GetOrCreateQueueAsync(db, user.Id);

            if (request.MediaItemId is not null && !queue.Items.Any(i => i.MediaItemId == request.MediaItemId))
            {
                return Results.BadRequest(new ApiErrorResponse("MediaItemId must already be in the queue."));
            }

            queue.CurrentMediaItemId = request.MediaItemId;
            queue.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("SetQueueCurrentTrack")
        .WithOpenApi();

        playlistGroup.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null || GetAccess(playlist, user.Id) == PlaylistAccess.None)
            {
                return Results.NotFound();
            }

            return Results.Ok(ToPlaylistDetailDto(playlist, user.Id));
        })
        .WithName("GetPlaylistById")
        .WithOpenApi();

        playlistGroup.MapPost("/", async (
            CreatePlaylistRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiErrorResponse("Name is required."));
            }

            var playlist = new Playlist
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Type = PlaylistType.Standard,
                OwnerId = user.Id,
                DateAdded = DateTime.UtcNow,
                LastModified = DateTime.UtcNow
            };

            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();

            playlist.Owner = user;
            return Results.Created($"/api/playlists/{playlist.Id}", ToPlaylistDto(playlist, user.Id));
        })
        .WithName("CreatePlaylist")
        .WithOpenApi();

        playlistGroup.MapPut("/{id:guid}", async (
            Guid id,
            UpdatePlaylistRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }

            var access = GetAccess(playlist, user.Id);
            if (access == PlaylistAccess.None)
            {
                return Results.NotFound();
            }
            if (access == PlaylistAccess.ReadOnly)
            {
                return Results.Forbid();
            }

            if (playlist.Type == PlaylistType.Queue)
            {
                return Results.BadRequest(new ApiErrorResponse("The queue cannot be renamed."));
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ApiErrorResponse("Name is required."));
            }

            playlist.Name = request.Name;
            playlist.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(ToPlaylistDto(playlist, user.Id));
        })
        .WithName("UpdatePlaylist")
        .WithOpenApi();

        playlistGroup.MapDelete("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
            if (playlist is null)
            {
                return Results.NotFound();
            }
            if (playlist.OwnerId != user.Id)
            {
                return Results.Forbid();
            }
            if (playlist.Type == PlaylistType.Queue)
            {
                return Results.BadRequest(new ApiErrorResponse("The queue cannot be deleted."));
            }

            db.Playlists.Remove(playlist);
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("DeletePlaylist")
        .WithOpenApi();

        playlistGroup.MapPost("/{id:guid}/items", async (
            Guid id,
            AddPlaylistItemsRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }

            var access = GetAccess(playlist, user.Id);
            if (access == PlaylistAccess.None)
            {
                return Results.NotFound();
            }
            if (access == PlaylistAccess.ReadOnly)
            {
                return Results.Forbid();
            }

            var mediaItemIds = request.MediaItemIds.ToList();
            if (mediaItemIds.Count == 0)
            {
                return Results.BadRequest(new ApiErrorResponse("At least one media item id is required."));
            }

            var existingMediaItemIds = await db.MediaItems
                .Where(m => mediaItemIds.Contains(m.Id))
                .Select(m => m.Id)
                .ToListAsync();

            var missingIds = mediaItemIds.Except(existingMediaItemIds).ToList();
            if (missingIds.Count > 0)
            {
                return Results.BadRequest(new ApiErrorResponse($"Unknown media item id(s): {string.Join(", ", missingIds)}"));
            }

            var nextPosition = playlist.Items.Count == 0 ? 0 : playlist.Items.Max(i => i.Position) + PositionGap;
            foreach (var mediaItemId in mediaItemIds)
            {
                db.PlaylistItems.Add(new PlaylistItem
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlist.Id,
                    MediaItemId = mediaItemId,
                    Position = nextPosition,
                    DateAdded = DateTime.UtcNow
                });
                nextPosition += PositionGap;
            }

            playlist.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var updated = await LoadPlaylistAsync(db, id);
            return Results.Ok(ToPlaylistDetailDto(updated!, user.Id));
        })
        .WithName("AddPlaylistItems")
        .WithOpenApi();

        playlistGroup.MapDelete("/{id:guid}/items/{itemId:guid}", async (
            Guid id,
            Guid itemId,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }

            var access = GetAccess(playlist, user.Id);
            if (access == PlaylistAccess.None)
            {
                return Results.NotFound();
            }
            if (access == PlaylistAccess.ReadOnly)
            {
                return Results.Forbid();
            }

            var item = playlist.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
            {
                return Results.NotFound();
            }

            db.PlaylistItems.Remove(item);
            playlist.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("RemovePlaylistItem")
        .WithOpenApi();

        playlistGroup.MapPut("/{id:guid}/items/{itemId:guid}/move", async (
            Guid id,
            Guid itemId,
            MovePlaylistItemRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }

            var access = GetAccess(playlist, user.Id);
            if (access == PlaylistAccess.None)
            {
                return Results.NotFound();
            }
            if (access == PlaylistAccess.ReadOnly)
            {
                return Results.Forbid();
            }

            var itemToMove = playlist.Items.FirstOrDefault(i => i.Id == itemId);
            if (itemToMove is null)
            {
                return Results.NotFound();
            }

            if (request.AfterItemId == itemId)
            {
                return Results.BadRequest(new ApiErrorResponse("An item cannot be moved after itself."));
            }

            var others = playlist.Items
                .Where(i => i.Id != itemId)
                .OrderBy(i => i.Position)
                .ToList();

            int insertIndex;
            if (request.AfterItemId is null)
            {
                insertIndex = 0;
            }
            else
            {
                var afterIndex = others.FindIndex(i => i.Id == request.AfterItemId);
                if (afterIndex == -1)
                {
                    return Results.BadRequest(new ApiErrorResponse("AfterItemId does not reference an item in this playlist."));
                }
                insertIndex = afterIndex + 1;
            }

            var newPosition = ComputePosition(others, insertIndex);

            // Fractional positions can only run out of precision after many
            // reorders squeezed into the same gap; renormalize as a fallback
            // so this stays correct without ever being the common-case cost.
            var precedingPosition = insertIndex > 0 ? others[insertIndex - 1].Position : (double?)null;
            var followingPosition = insertIndex < others.Count ? others[insertIndex].Position : (double?)null;
            if ((precedingPosition.HasValue && newPosition <= precedingPosition.Value) ||
                (followingPosition.HasValue && newPosition >= followingPosition.Value))
            {
                for (var i = 0; i < others.Count; i++)
                {
                    others[i].Position = i * PositionGap;
                }
                newPosition = ComputePosition(others, insertIndex);
            }

            itemToMove.Position = newPosition;
            playlist.LastModified = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(ToPlaylistDetailDto(playlist, user.Id));
        })
        .WithName("MovePlaylistItem")
        .WithOpenApi();

        playlistGroup.MapPost("/{id:guid}/shares", async (
            Guid id,
            SharePlaylistRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }
            if (playlist.OwnerId != user.Id)
            {
                return Results.Forbid();
            }
            if (playlist.Type == PlaylistType.Queue)
            {
                return Results.BadRequest(new ApiErrorResponse("The queue cannot be shared."));
            }

            if (!Enum.TryParse<PlaylistShareType>(request.ShareType, ignoreCase: true, out var shareType))
            {
                return Results.BadRequest(new ApiErrorResponse("ShareType must be 'Editor' or 'ReadOnly'."));
            }

            if (request.UserId == user.Id)
            {
                return Results.BadRequest(new ApiErrorResponse("Cannot share a playlist with its owner."));
            }

            var targetUser = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
            if (targetUser is null)
            {
                return Results.BadRequest(new ApiErrorResponse("Target user does not exist."));
            }

            var share = playlist.Shares.FirstOrDefault(s => s.UserId == request.UserId);
            if (share is null)
            {
                share = new PlaylistShare
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = playlist.Id,
                    UserId = request.UserId,
                    ShareType = shareType,
                    DateAdded = DateTime.UtcNow
                };
                db.PlaylistShares.Add(share);
            }
            else
            {
                share.ShareType = shareType;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new PlaylistShareDto(targetUser.Id, targetUser.DisplayName, shareType.ToString()));
        })
        .WithName("SharePlaylist")
        .WithOpenApi();

        playlistGroup.MapDelete("/{id:guid}/shares/{userId}", async (
            Guid id,
            string userId,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var playlist = await LoadPlaylistAsync(db, id);
            if (playlist is null)
            {
                return Results.NotFound();
            }
            if (playlist.OwnerId != user.Id)
            {
                return Results.Forbid();
            }

            var share = playlist.Shares.FirstOrDefault(s => s.UserId == userId);
            if (share is null)
            {
                return Results.NotFound();
            }

            db.PlaylistShares.Remove(share);
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("RevokePlaylistShare")
        .WithOpenApi();
    }

    // Gap between fractional positions. Leaves room to insert between any two
    // neighbors by averaging, without needing to touch other rows.
    private const double PositionGap = 1024;

    private static double ComputePosition(IReadOnlyList<PlaylistItem> orderedOthers, int insertIndex)
    {
        var preceding = insertIndex > 0 ? orderedOthers[insertIndex - 1].Position : (double?)null;
        var following = insertIndex < orderedOthers.Count ? orderedOthers[insertIndex].Position : (double?)null;

        return (preceding, following) switch
        {
            (null, null) => 0,
            (null, { } f) => f - PositionGap,
            ({ } p, null) => p + PositionGap,
            ({ } p, { } f) => (p + f) / 2
        };
    }

    private static Task<Playlist?> LoadPlaylistAsync(FossegrimDbContext db, Guid id) =>
        db.Playlists
            .Include(p => p.Owner)
            .Include(p => p.Shares).ThenInclude(s => s.User)
            .Include(p => p.Items).ThenInclude(i => i.MediaItem)
            .FirstOrDefaultAsync(p => p.Id == id);

    private static IQueryable<Playlist> QueueQuery(FossegrimDbContext db, string userId) =>
        db.Playlists
            .Include(p => p.Owner)
            .Include(p => p.Shares).ThenInclude(s => s.User)
            .Include(p => p.Items).ThenInclude(i => i.MediaItem)
            .Where(p => p.OwnerId == userId && p.Type == PlaylistType.Queue);

    private static async Task<Playlist> GetOrCreateQueueAsync(FossegrimDbContext db, string userId)
    {
        var queue = await QueueQuery(db, userId).FirstOrDefaultAsync();
        if (queue is not null)
        {
            return queue;
        }

        queue = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Queue",
            Type = PlaylistType.Queue,
            OwnerId = userId,
            DateAdded = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };
        db.Playlists.Add(queue);

        try
        {
            await db.SaveChangesAsync();
            return queue;
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent request creating this user's queue.
            // The unique index guarantees exactly one exists, so just load it.
            db.Entry(queue).State = EntityState.Detached;
            return await QueueQuery(db, userId).FirstAsync();
        }
    }

    private enum PlaylistAccess
    {
        None,
        ReadOnly,
        Editor,
        Owner
    }

    private static PlaylistAccess GetAccess(Playlist playlist, string userId)
    {
        if (playlist.OwnerId == userId)
        {
            return PlaylistAccess.Owner;
        }

        var share = playlist.Shares.FirstOrDefault(s => s.UserId == userId);
        if (share is null)
        {
            return PlaylistAccess.None;
        }

        return share.ShareType == PlaylistShareType.Editor ? PlaylistAccess.Editor : PlaylistAccess.ReadOnly;
    }

    private static PlaylistDto ToPlaylistDto(Playlist p, string currentUserId) => new(
        p.Id,
        p.Name,
        p.Type.ToString(),
        p.OwnerId,
        p.Owner?.DisplayName,
        GetAccess(p, currentUserId).ToString(),
        p.DateAdded,
        p.LastModified,
        p.Items.Count);

    private static PlaylistDetailDto ToPlaylistDetailDto(Playlist p, string currentUserId) => new(
        p.Id,
        p.Name,
        p.Type.ToString(),
        p.OwnerId,
        p.Owner?.DisplayName,
        GetAccess(p, currentUserId).ToString(),
        p.DateAdded,
        p.LastModified,
        p.CurrentMediaItemId,
        p.Items
            .OrderBy(i => i.Position)
            .Select(i => new PlaylistItemDto(
                i.Id,
                i.Position,
                i.DateAdded,
                new PlaylistTrackDto(i.MediaItem!.Id, i.MediaItem.Title, i.MediaItem.ArtistName, i.MediaItem.Album, i.MediaItem.Duration))),
        p.Shares.Select(s => new PlaylistShareDto(s.UserId, s.User?.DisplayName, s.ShareType.ToString())));
}

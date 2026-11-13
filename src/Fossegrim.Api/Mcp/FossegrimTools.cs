using System.ComponentModel;
using System.Security.Claims;
using Fossegrim.Api.Endpoints;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Fossegrim.Api.Endpoints.Playlists;

namespace Fossegrim.Api.Mcp;

/// <summary>
/// The MCP tools: the caller's view of the library (search, lists, albums,
/// artists, their plays and favourites) and their playlists. Everything runs
/// as the user whose access token signed the request, with the same rules as
/// the web API. Playback itself happens in the browser, so there's nothing
/// here to play or pause.
/// </summary>
[McpServerToolType]
public class FossegrimTools
{
    private const int MaxPageSize = 200;

    private readonly FossegrimDbContext _db;
    private readonly LibraryBrowser _browser;
    private readonly LibraryInfoService _info;
    private readonly TimeProvider _time;
    private readonly string _userId;

    public FossegrimTools(FossegrimDbContext db, LibraryBrowser browser, LibraryInfoService info, TimeProvider time, IHttpContextAccessor http)
    {
        _db = db;
        _browser = browser;
        _info = info;
        _time = time;
        _userId = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new McpException("Not signed in.");
    }

    // ---- Library ----

    [McpServerTool(Name = "search_library", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search the music library. Returns the best-matching artists, albums and tracks (each word must match).")]
    public async Task<SearchResults> SearchLibrary(
        [Description("Words to search for, e.g. an artist, album or song name.")] string query,
        [Description("How many of each kind to return (1-50).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (SearchIndex.MatchQuery(query) is null)
        {
            throw new McpException("The query must contain a word to search for.");
        }
        var size = Math.Clamp(limit, 1, 50);
        BrowseRequest Request(IReadOnlyList<string> sorts) => LibraryBrowser.Parse(query, null, null, 1, size, sorts).Request!;

        var artists = await _browser.ArtistsAsync(Request(LibrarySorts.ForArtists), _userId, cancellationToken);
        var albums = await _browser.AlbumsAsync(Request(LibrarySorts.ForAlbums), _userId, cancellationToken);
        var tracks = await _browser.TracksAsync(Request(LibrarySorts.ForTracks), _userId, cancellationToken);
        return new SearchResults(
            artists.Items.Select(McpArtist.From).ToList(),
            albums.Items.Select(McpAlbum.From).ToList(),
            tracks.Items.Select(McpTrack.From).ToList());
    }

    [McpServerTool(Name = "list_tracks", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List tracks a page at a time, optionally filtered and sorted. Sorts: title, artist, album, year, added, played (play count), recent (last played), favorited, relevance.")]
    public async Task<McpPage<McpTrack>> ListTracks(
        [Description("Only tracks matching these words.")] string? query = null,
        [Description("Sort: title, artist, album, year, added, played, recent, favorited or relevance.")] string? sort = null,
        [Description("asc or desc. Defaults to newest/most first for dates and counts, A-Z for names.")] string? order = null,
        [Description("Only the user's favourite tracks.")] bool favoritesOnly = false,
        [Description("Only tracks the user has played.")] bool playedOnly = false,
        int page = 1,
        [Description("Items per page (1-200).")] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        McpPage<McpTrack>.From(await _browser.TracksAsync(Parse(query, sort, order, page, pageSize, LibrarySorts.ForTracks, playedOnly, favoritesOnly), _userId, cancellationToken), McpTrack.From);

    [McpServerTool(Name = "list_albums", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List albums a page at a time, optionally filtered and sorted. Sorts: name, artist, year, added, played (times played all the way through), recent (last played), favorited, relevance. Plays counts only full listens: every track heard.")]
    public async Task<McpPage<McpAlbum>> ListAlbums(
        [Description("Only albums matching these words.")] string? query = null,
        [Description("Sort: name, artist, year, added, played, recent, favorited or relevance.")] string? sort = null,
        [Description("asc or desc. Defaults to newest/most first for dates and counts, A-Z for names.")] string? order = null,
        [Description("Only the user's favourite albums.")] bool favoritesOnly = false,
        [Description("Only albums the user has played.")] bool playedOnly = false,
        int page = 1,
        [Description("Items per page (1-200).")] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        McpPage<McpAlbum>.From(await _browser.AlbumsAsync(Parse(query, sort, order, page, pageSize, LibrarySorts.ForAlbums, playedOnly, favoritesOnly), _userId, cancellationToken), McpAlbum.From);

    [McpServerTool(Name = "list_artists", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List artists a page at a time, optionally filtered and sorted. Sorts: name, added, played (play count), recent (last played), favorited, relevance.")]
    public async Task<McpPage<McpArtist>> ListArtists(
        [Description("Only artists matching these words.")] string? query = null,
        [Description("Sort: name, added, played, recent, favorited or relevance.")] string? sort = null,
        [Description("asc or desc. Defaults to newest/most first for dates and counts, A-Z for names.")] string? order = null,
        [Description("Only the user's favourite artists.")] bool favoritesOnly = false,
        [Description("Only artists the user has played.")] bool playedOnly = false,
        int page = 1,
        [Description("Items per page (1-200).")] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        McpPage<McpArtist>.From(await _browser.ArtistsAsync(Parse(query, sort, order, page, pageSize, LibrarySorts.ForArtists, playedOnly, favoritesOnly), _userId, cancellationToken), McpArtist.From);

    [McpServerTool(Name = "get_album", ReadOnly = true, OpenWorld = true)]
    [Description("An album's details, its tracks in play order, how many times the user has played it all the way through, and a Wikipedia summary when one is known.")]
    public async Task<AlbumDetails> GetAlbum(Guid albumId, CancellationToken cancellationToken = default)
    {
        var info = await _info.AlbumAsync(albumId, _userId, lookup: true, cancellationToken)
            ?? throw new McpException($"No album with id {albumId}.");
        var tracks = await _db.MediaItems
            .Where(m => m.Albums.Any(al => al.Id == albumId))
            .Select(MediaItemMapping.ToDto)
            .ToListAsync(cancellationToken);
        return new AlbumDetails(info.Id, info.Name, info.Artists, info.Year, info.Genre, info.Label, Duration(info.Length),
            info.Plays, info.LastPlayedAt, info.Summary, info.InfoUrl, tracks.InPlayOrder().Select(McpTrack.From).ToList());
    }

    [McpServerTool(Name = "get_artist", ReadOnly = true, OpenWorld = true)]
    [Description("An artist's details, their albums (oldest first), the user's plays of them, and a Wikipedia summary when one is known.")]
    public async Task<ArtistDetails> GetArtist(Guid artistId, CancellationToken cancellationToken = default)
    {
        var info = await _info.ArtistAsync(artistId, _userId, lookup: true, cancellationToken)
            ?? throw new McpException($"No artist with id {artistId}.");
        var albums = await _db.Albums
            .Where(al => al.Artists.Any(a => a.Id == artistId))
            .Select(al => new McpAlbumRef(al.Id, al.Name, al.Year, al.MediaItems.Count))
            .ToListAsync(cancellationToken);
        return new ArtistDetails(info.Id, info.Name, info.Country, info.TrackCount, info.Plays, info.LastPlayedAt, info.Summary, info.InfoUrl,
            albums.OrderBy(a => a.Year ?? int.MaxValue).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    [McpServerTool(Name = "get_play_history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The user's listening history, newest first, a page at a time. Times are UTC.")]
    public async Task<McpPage<McpPlay>> GetPlayHistory(
        [Description("Only plays whose track matches every word (title, artist, album, genre, year).")] string? query = null,
        int page = 1,
        [Description("Items per page (1-200).")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var number = Math.Max(page, 1);
        var plays = _db.PlayEvents.Where(pe => pe.UserId == _userId);
        foreach (var term in Plays.SearchTerms(query))
        {
            plays = plays.Where(Plays.MatchesTerm(term));
        }

        var total = await plays.CountAsync(cancellationToken);
        var items = await plays
            .OrderByDescending(pe => pe.PlayedAt).ThenByDescending(pe => pe.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(pe => new McpPlay(pe.PlayedAt, pe.MediaItemId, pe.MediaItem!.Title, pe.MediaItem.ArtistName, pe.MediaItem.Album))
            .ToListAsync(cancellationToken);
        return new McpPage<McpPlay>(items, total, number, (total + size - 1) / size);
    }

    [McpServerTool(Name = "get_library_stats", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How big the library is, and how much the user has played and favourited.")]
    public async Task<LibraryStats> GetLibraryStats(CancellationToken cancellationToken = default) => new(
        await _db.Artists.CountAsync(cancellationToken),
        await _db.Albums.CountAsync(cancellationToken),
        await _db.MediaItems.CountAsync(cancellationToken),
        await _db.PlayEvents.CountAsync(pe => pe.UserId == _userId, cancellationToken),
        await _db.FavoriteTracks.CountAsync(f => f.UserId == _userId, cancellationToken),
        await _db.FavoriteAlbums.CountAsync(f => f.UserId == _userId, cancellationToken),
        await _db.FavoriteArtists.CountAsync(f => f.UserId == _userId, cancellationToken));

    // ---- Favourites ----

    [McpServerTool(Name = "set_favorite", Idempotent = true, OpenWorld = false)]
    [Description("Make a track, album or artist one of the user's favourites, or stop it being one.")]
    public async Task<string> SetFavorite(
        [Description("tracks, albums or artists.")] string kind,
        [Description("The track, album or artist id.")] Guid id,
        [Description("true to favourite it, false to unfavourite it.")] bool favorite = true)
    {
        var change = await Favorites.SetAsync(_db, _userId, kind, id, favorite, _time.GetUtcNow().UtcDateTime);
        return change switch
        {
            Favorites.FavoriteChange.UnknownKind => throw new McpException("kind must be tracks, albums or artists."),
            Favorites.FavoriteChange.NotFound => throw new McpException($"No {kind.TrimEnd('s')} with id {id}."),
            _ => favorite ? "Added to favourites." : "Removed from favourites.",
        };
    }

    // ---- Playlists ----

    [McpServerTool(Name = "list_playlists", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The user's playlists and those shared with them. Access is Owner, Editor (can change it) or ReadOnly.")]
    public async Task<List<McpPlaylist>> ListPlaylists(CancellationToken cancellationToken = default)
    {
        var playlists = await _db.Playlists
            .Include(p => p.Owner).Include(p => p.Shares).Include(p => p.Items)
            .Where(p => p.Type != PlaylistType.Queue)
            .Where(p => p.OwnerId == _userId || p.Shares.Any(s => s.UserId == _userId))
            .ToListAsync(cancellationToken);
        return playlists.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(Summary).ToList();
    }

    [McpServerTool(Name = "get_playlist", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A playlist and its tracks in order. Each entry has an entryId, used to remove it.")]
    public async Task<McpPlaylistDetails> GetPlaylist(Guid playlistId) =>
        Details(await LoadAsync(playlistId, PlaylistAccess.ReadOnly));

    [McpServerTool(Name = "create_playlist", OpenWorld = false)]
    [Description("Create a playlist owned by the user, optionally with tracks in it already.")]
    public async Task<McpPlaylistDetails> CreatePlaylist(
        string name,
        [Description("Track ids to put in it, in order.")] List<Guid>? trackIds = null)
    {
        if (NameError(name) is { } error)
        {
            throw new McpException(error);
        }
        var now = _time.GetUtcNow().UtcDateTime;
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Type = PlaylistType.Standard,
            OwnerId = _userId,
            DateAdded = now,
            LastModified = now,
        };

        // Check the tracks before saving anything, so a bad id leaves no empty playlist behind.
        if (trackIds is { Count: > 0 } && await MissingTracksAsync(trackIds) is { } missing)
        {
            throw new McpException(missing);
        }
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();
        if (trackIds is { Count: > 0 })
        {
            await AddItemsAsync(_db, playlist, trackIds);
        }
        return Details(await LoadAsync(playlist.Id, PlaylistAccess.Owner));
    }

    [McpServerTool(Name = "add_to_playlist", OpenWorld = false)]
    [Description("Add tracks to the end of a playlist the user owns or can edit.")]
    public async Task<McpPlaylistDetails> AddToPlaylist(Guid playlistId, [Description("Track ids, in order.")] List<Guid> trackIds)
    {
        var playlist = await LoadAsync(playlistId, PlaylistAccess.Editor);
        if (await AddItemsAsync(_db, playlist, trackIds) is { } error)
        {
            throw new McpException(error);
        }
        return Details(await LoadAsync(playlistId, PlaylistAccess.Editor));
    }

    [McpServerTool(Name = "remove_from_playlist", Destructive = true, OpenWorld = false)]
    [Description("Remove entries from a playlist the user owns or can edit. Use the entryIds from get_playlist (not track ids: a track can be in a playlist twice).")]
    public async Task<McpPlaylistDetails> RemoveFromPlaylist(Guid playlistId, List<Guid> entryIds)
    {
        var playlist = await LoadAsync(playlistId, PlaylistAccess.Editor);
        var unknown = entryIds.Where(id => playlist.Items.All(i => i.Id != id)).ToList();
        if (entryIds.Count == 0 || unknown.Count > 0)
        {
            throw new McpException(entryIds.Count == 0
                ? "Give at least one entryId."
                : $"Not entries in this playlist: {string.Join(", ", unknown)}. Use the entryIds from get_playlist.");
        }

        _db.PlaylistItems.RemoveRange(playlist.Items.Where(i => entryIds.Contains(i.Id)));
        playlist.LastModified = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        return Details(await LoadAsync(playlistId, PlaylistAccess.Editor));
    }

    [McpServerTool(Name = "rename_playlist", Idempotent = true, OpenWorld = false)]
    [Description("Rename a playlist the user owns or can edit.")]
    public async Task<McpPlaylist> RenamePlaylist(Guid playlistId, string name)
    {
        if (NameError(name) is { } error)
        {
            throw new McpException(error);
        }
        var playlist = await LoadAsync(playlistId, PlaylistAccess.Editor);
        playlist.Name = name.Trim();
        playlist.LastModified = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        return Summary(playlist);
    }

    [McpServerTool(Name = "delete_playlist", Destructive = true, OpenWorld = false)]
    [Description("Delete a playlist the user owns. This can't be undone.")]
    public async Task<string> DeletePlaylist(Guid playlistId)
    {
        var playlist = await LoadAsync(playlistId, PlaylistAccess.Owner);
        _db.Playlists.Remove(playlist);
        await _db.SaveChangesAsync();
        return $"Deleted “{playlist.Name}”.";
    }

    // ---- Helpers ----

    private static BrowseRequest Parse(string? query, string? sort, string? order, int page, int pageSize, IReadOnlyList<string> sorts, bool played, bool favorites)
    {
        var (request, error) = LibraryBrowser.Parse(query, sort, order, Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), sorts, played, favorites);
        return request ?? throw new McpException(error!);
    }

    /// <summary>The playlist, if the user has at least <paramref name="needed"/> access to it. The queue isn't reachable here.</summary>
    private async Task<Playlist> LoadAsync(Guid id, PlaylistAccess needed)
    {
        var playlist = await LoadPlaylistAsync(_db, id);
        var access = playlist is null || playlist.Type == PlaylistType.Queue ? PlaylistAccess.None : GetAccess(playlist, _userId);
        if (access == PlaylistAccess.None)
        {
            throw new McpException($"No playlist with id {id}.");
        }
        if (access < needed)
        {
            throw new McpException(needed == PlaylistAccess.Owner
                ? "Only the playlist's owner can do that."
                : "This playlist was shared read-only, so it can't be changed.");
        }
        return playlist!;
    }

    private async Task<string?> MissingTracksAsync(List<Guid> trackIds)
    {
        var found = await _db.MediaItems.Where(m => trackIds.Contains(m.Id)).Select(m => m.Id).ToListAsync();
        var missing = trackIds.Except(found).ToList();
        return missing.Count == 0 ? null : $"Unknown track id(s): {string.Join(", ", missing)}";
    }

    private McpPlaylist Summary(Playlist p) =>
        new(p.Id, p.Name, p.Owner?.UserName, GetAccess(p, _userId).ToString(), p.Items.Count, p.LastModified);

    private McpPlaylistDetails Details(Playlist p) => new(
        p.Id, p.Name, p.Owner?.UserName, GetAccess(p, _userId).ToString(), p.LastModified,
        p.Items.OrderBy(i => i.Position)
            .Select(i => new McpPlaylistEntry(i.Id, i.MediaItemId, i.MediaItem?.Title, i.MediaItem?.ArtistName, i.MediaItem?.Album, Duration(i.MediaItem?.Duration)))
            .ToList());

    internal static string? Duration(TimeSpan? length) => length is not { } d ? null
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
        : $"{d.Minutes}:{d.Seconds:00}";
}

// What the tools return: compact versions of the API's DTOs, so results stay small in a model's context.

public record McpPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int TotalPages)
{
    public static McpPage<T> From<TSource>(PagedResult<TSource> page, Func<TSource, T> map) =>
        new(page.Items.Select(map).ToList(), page.TotalCount, page.Page, page.TotalPages);
}

public record SearchResults(IReadOnlyList<McpArtist> Artists, IReadOnlyList<McpAlbum> Albums, IReadOnlyList<McpTrack> Tracks);

public record McpTrack(Guid Id, string? Title, string? Artist, string? Album, Guid? AlbumId, int? Year, int? Disc, int? Track, string? Duration, string? Genre, DateTime? LastPlayedAt)
{
    public static McpTrack From(MediaItemDto m) => new(
        m.Id, m.Title, m.ArtistName, m.Album, m.Albums.FirstOrDefault()?.Id, m.Year, m.Disc, m.Track,
        FossegrimTools.Duration(m.Duration), m.Genre, m.LastPlayedAt);
}

// Plays: times the user has heard every track on it. LastPlayedAt: their latest play of any of its tracks.
public record McpAlbum(Guid Id, string Name, IReadOnlyList<string> Artists, int? Year, string? Genre, int TrackCount, int Plays, DateTime? LastPlayedAt)
{
    public static McpAlbum From(AlbumListItemDto a) => new(a.Id, a.Name, a.Artists.Select(x => x.Name).ToList(), a.Year, a.Genre, a.TrackCount, a.Plays, a.LastPlayedAt);
}

public record McpArtist(Guid Id, string Name, int AlbumCount, int TrackCount, DateTime? LastPlayedAt)
{
    public static McpArtist From(ArtistListItemDto a) => new(a.Id, a.Name, a.AlbumCount, a.TrackCount, a.LastPlayedAt);
}

public record McpAlbumRef(Guid Id, string Name, int? Year, int TrackCount);

public record AlbumDetails(
    Guid Id, string Name, IReadOnlyList<string> Artists, int? Year, string? Genre, string? Label, string? Length,
    int Plays, DateTime? LastPlayedAt, string? WikipediaSummary, string? WikipediaUrl, IReadOnlyList<McpTrack> Tracks);

public record ArtistDetails(
    Guid Id, string Name, string? Country, int TrackCount, int Plays, DateTime? LastPlayedAt,
    string? WikipediaSummary, string? WikipediaUrl, IReadOnlyList<McpAlbumRef> Albums);

public record McpPlay(DateTime PlayedAt, Guid TrackId, string? Title, string? Artist, string? Album);

public record LibraryStats(int Artists, int Albums, int Tracks, int YourPlays, int YourFavoriteTracks, int YourFavoriteAlbums, int YourFavoriteArtists);

public record McpPlaylist(Guid Id, string Name, string? Owner, string Access, int TrackCount, DateTime? LastModified);

public record McpPlaylistEntry(Guid EntryId, Guid TrackId, string? Title, string? Artist, string? Album, string? Duration);

public record McpPlaylistDetails(Guid Id, string Name, string? Owner, string Access, DateTime? LastModified, IReadOnlyList<McpPlaylistEntry> Entries);

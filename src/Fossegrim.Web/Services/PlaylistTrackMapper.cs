using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

public static class PlaylistTrackMapper
{
    public static MediaItemDto ToMediaItemDto(PlaylistTrackDto track) => new(
        track.Id,
        track.Title,
        track.ArtistName,
        track.Album,
        null,
        null,
        null,
        null,
        null,
        track.Duration,
        null,
        [],
        // The album's id lets the player tell same-named albums apart (see PlaybackTransitions).
        track.AlbumId is { } albumId ? [new AlbumSummaryDto(albumId, track.Album ?? "", null)] : []);
}

using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

public class MediaItemTagEditModel
{
    public Guid Id { get; set; }
    // Read-only: shown so you can tell which file you're editing.
    public string? FileName { get; init; }
    public string? Folder { get; init; }
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? Album { get; set; }
    public int? Track { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }

    public static MediaItemTagEditModel FromMediaItem(MediaItemDto item) => new()
    {
        Id = item.Id,
        FileName = item.FileName,
        Folder = item.Folder,
        Title = item.Title,
        ArtistName = item.ArtistName,
        Album = item.Album,
        Track = item.Track,
        Year = item.Year,
        Genre = item.Genre
    };

    public MediaItemTagUpdate ToUpdateRequest() => new(Id, Title, ArtistName, Album, Track, Year, Genre);
}

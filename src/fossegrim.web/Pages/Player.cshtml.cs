using Fossegrim.Lib.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fossegrim.Web.Pages;

public class PlayerModel : PageModel
{
    private readonly FossegrimDbContext _db;

    public PlayerModel(FossegrimDbContext db)
    {
        _db = db;
    }

    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        var mediaItem = await _db.MediaItems.FindAsync(id);

        if (mediaItem is null || string.IsNullOrWhiteSpace(mediaItem.FileLocation) || !System.IO.File.Exists(mediaItem.FileLocation))
        {
            ErrorMessage = "Track not found.";
            return;
        }

        Title = mediaItem.Title;
        Artist = mediaItem.ArtistName;
        Album = mediaItem.Album;
        StreamUrl = $"/stream/{mediaItem.Id}";
    }
}

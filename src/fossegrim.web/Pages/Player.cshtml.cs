using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fossegrim.Web.Pages;

[Authorize]
public class PlayerModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;

    public PlayerModel(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        var mediaItem = await _apiClient.GetMediaItemAsync(HttpContext, id);
        if (mediaItem is null)
        {
            ErrorMessage = "Track not found.";
            return;
        }

        Title = mediaItem.Title;
        Artist = mediaItem.ArtistName;
        Album = mediaItem.Album;

        StreamUrl = await _apiClient.GetStreamUrlAsync(HttpContext, mediaItem.Id);
    }
}

using Fossegrim.Lib.Dtos;
using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fossegrim.Web.ViewComponents;

public class ArtistListViewComponent : ViewComponent
{
    private readonly FossegrimApiClient _apiClient;

    public ArtistListViewComponent(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var artists = await _apiClient.GetArtistsAsync(HttpContext);
        return View(new ArtistListViewModel(artists.OrderBy(a => a.Name).ToList()));
    }
}

public class ArtistListViewModel
{
    public ArtistListViewModel(IReadOnlyList<ArtistDto> artists)
    {
        Artists = artists;
    }

    public IReadOnlyList<ArtistDto> Artists { get; }
}

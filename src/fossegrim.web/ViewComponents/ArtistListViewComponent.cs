using Microsoft.AspNetCore.Mvc;

namespace Fossegrim.Web.ViewComponents;

public class ArtistListViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        // Component now fetches data client-side from API
        return View(new ArtistListViewModel());
    }
}

public class ArtistListViewModel
{
    // Empty model - data loaded via JavaScript from API
}

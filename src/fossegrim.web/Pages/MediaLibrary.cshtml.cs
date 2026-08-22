using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;

namespace Fossegrim.Web.Pages;

public class MediaLibraryModel : PageModel
{
    private readonly FossegrimDbContext _db;

    public MediaLibraryModel(FossegrimDbContext db)
    {
        _db = db;
    }

    public IEnumerable<MediaItem> MediaItems { get; set; } = Enumerable.Empty<MediaItem>();
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            MediaItems = await _db.MediaItems.OrderBy(m => m.Title).ToListAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading media library: {ex.Message}";
        }
    }
}

using Fossegrim.Lib.Dtos;
using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fossegrim.Web.Pages;

[Authorize]
public class MediaLibraryModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;

    public MediaLibraryModel(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public IEnumerable<MediaItemDto> MediaItems { get; set; } = [];
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            MediaItems = await _apiClient.GetMediaItemsAsync(HttpContext);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading media library: {ex.Message}";
        }
    }
}

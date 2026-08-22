using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Fossegrim.Lib.Services;

namespace Fossegrim.Web.Pages.Settings;

[Authorize(Roles = "Admin")]
public class AdminModel : PageModel
{
    private readonly MediaLibraryService _mediaLibraryService;

    public AdminModel(MediaLibraryService mediaLibraryService)
    {
        _mediaLibraryService = mediaLibraryService;
    }

    [BindProperty]
    public string FolderPath { get; set; } = "/Users/tj/Documents/Music";

    [BindProperty]
    public int MaxDegreeOfParallelism { get; set; } = 4;

    public bool IsScanning { get; set; }
    public ScanResult? LastScanResult { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet(string? folderPath)
    {
        if (!string.IsNullOrEmpty(folderPath))
        {
            FolderPath = folderPath;
        }
    }

    public async Task<IActionResult> OnPostScanAsync()
    {
        IsScanning = true;

        try
        {
            LastScanResult = await _mediaLibraryService.ScanAndSaveMediaItemsAsync(
                FolderPath,
                MaxDegreeOfParallelism);

            IsScanning = false;
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Scan failed: {ex.Message}";
            IsScanning = false;
            return Page();
        }
    }
}

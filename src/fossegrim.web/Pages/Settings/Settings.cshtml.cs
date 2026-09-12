using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Web.Pages.Settings;

[Authorize(Roles = "Admin")]
public class SettingsModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;
    private readonly ILogger<SettingsModel> _logger;

    public SettingsModel(FossegrimApiClient apiClient, ILogger<SettingsModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public MediaFolderInput NewFolder { get; set; } = new();

    public string? StatusMessage { get; set; }
    public bool IsSuccess { get; set; }

    public class InputModel
    {
        [Display(Name = "Media Folders")]
        public List<MediaFolderInput> Folders { get; set; } = new();
    }

    public class MediaFolderInput
    {
        public Guid? Id { get; set; }

        [Required]
        public string Location { get; set; } = string.Empty;

        [Required]
        public string Name { get; set; } = string.Empty;
    }

    public async Task OnGetAsync()
    {
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            var folders = await _apiClient.GetMediaFoldersAsync(HttpContext);

            Input = new InputModel
            {
                Folders = folders.Select(f => new MediaFolderInput
                {
                    Id = f.Id,
                    Location = f.Location,
                    Name = f.Name
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load media folders from Api");
            StatusMessage = "Failed to load existing media folders.";
            IsSuccess = false;
        }
    }

    public async Task<IActionResult> OnPostAddFolderAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadConfigurationAsync();
            StatusMessage = "Please fill in all required fields.";
            IsSuccess = false;
            return Page();
        }

        var result = await _apiClient.AddMediaFolderAsync(HttpContext, NewFolder.Name, NewFolder.Location);

        if (!result.Success)
        {
            await LoadConfigurationAsync();
            StatusMessage = result.ErrorMessage;
            IsSuccess = false;
            return Page();
        }

        _logger.LogInformation("Added new media folder: {Name} at {Location}", NewFolder.Name, NewFolder.Location);
        StatusMessage = $"Successfully added folder '{result.Folder!.Name}'.";
        IsSuccess = true;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveFolderAsync(Guid id)
    {
        try
        {
            await _apiClient.DeleteMediaFolderAsync(HttpContext, id);

            _logger.LogInformation("Removed media folder {Id}", id);
            StatusMessage = "Successfully removed folder.";
            IsSuccess = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove media folder");
            StatusMessage = $"Failed to remove folder: {ex.Message}";
            IsSuccess = false;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScanFolderAsync(Guid id)
    {
        try
        {
            var folder = await _apiClient.GetMediaFolderAsync(HttpContext, id);

            if (folder is not null)
            {
                return RedirectToPage("/Settings/Admin", new { folderPath = folder.Location });
            }

            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get folder for scanning");
            StatusMessage = $"Failed to initiate scan: {ex.Message}";
            IsSuccess = false;
            return RedirectToPage();
        }
    }
}

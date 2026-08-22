using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Web.Pages.Settings;

[Authorize(Roles = "Admin")]
public class SettingsModel : PageModel
{
    private readonly FossegrimDbContext _context;
    private readonly ILogger<SettingsModel> _logger;

    public SettingsModel(FossegrimDbContext context, ILogger<SettingsModel> logger)
    {
        _context = context;
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
            var folders = await _context.MediaFolders
                .OrderBy(f => f.Name)
                .ToListAsync();

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
            _logger.LogError(ex, "Failed to load media folders from database");
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

        try
        {
            // Check if folder already exists
            var existingFolder = await _context.MediaFolders
                .FirstOrDefaultAsync(f => f.Location == NewFolder.Location);

            if (existingFolder != null)
            {
                await LoadConfigurationAsync();
                StatusMessage = "A folder with this location already exists.";
                IsSuccess = false;
                return Page();
            }

            var folder = new MediaFolder
            {
                Location = NewFolder.Location,
                Name = NewFolder.Name,
                DateAdded = DateTime.UtcNow
            };

            _context.MediaFolders.Add(folder);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Added new media folder: {Name} at {Location}", folder.Name, folder.Location);
            StatusMessage = $"Successfully added folder '{folder.Name}'.";
            IsSuccess = true;

            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add media folder");
            StatusMessage = $"Failed to add folder: {ex.Message}";
            IsSuccess = false;
            await LoadConfigurationAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRemoveFolderAsync(Guid id)
    {
        try
        {
            var folder = await _context.MediaFolders.FindAsync(id);

            if (folder != null)
            {
                _context.MediaFolders.Remove(folder);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Removed media folder: {Name} at {Location}", folder.Name, folder.Location);
                StatusMessage = $"Successfully removed folder '{folder.Name}'.";
                IsSuccess = true;
            }

            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove media folder");
            StatusMessage = $"Failed to remove folder: {ex.Message}";
            IsSuccess = false;
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostScanFolderAsync(Guid id)
    {
        try
        {
            var folder = await _context.MediaFolders.FindAsync(id);

            if (folder != null)
            {
                // Redirect to Admin page with the folder path to scan
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

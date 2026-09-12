using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Web.Pages.Account;

[Authorize]
public class ChangePasswordModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;
    private readonly ILogger<ChangePasswordModel> _logger;

    public ChangePasswordModel(FossegrimApiClient apiClient, ILogger<ChangePasswordModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string OldPassword { get; set; } = string.Empty;

        [Required]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare("NewPassword", ErrorMessage = "The new password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public IActionResult OnGet()
    {
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var errors = await _apiClient.ChangePasswordAsync(HttpContext, Input.OldPassword, Input.NewPassword);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
            return Page();
        }

        _logger.LogInformation("User changed their password successfully.");
        StatusMessage = "Your password has been changed.";

        return RedirectToPage();
    }
}

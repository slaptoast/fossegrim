using Fossegrim.Lib.Dtos;
using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Web.Pages.Account;

[Authorize]
public class ProfileModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;

    public ProfileModel(FossegrimApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Display(Name = "Display Name")]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} characters long.", MinimumLength = 3)]
        public string? DisplayName { get; set; }

        [Phone]
        [Display(Name = "Phone number")]
        public string? PhoneNumber { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var profile = await _apiClient.GetProfileAsync(HttpContext);
        if (profile is null)
        {
            return Challenge();
        }

        LoadFrom(profile);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            var profile = await _apiClient.GetProfileAsync(HttpContext);
            if (profile is not null)
            {
                Username = profile.UserName;
                Email = profile.Email;
            }

            return Page();
        }

        var updated = await _apiClient.UpdateProfileAsync(HttpContext, Input.DisplayName, Input.PhoneNumber);
        LoadFrom(updated);
        StatusMessage = "Your profile has been updated";
        return RedirectToPage();
    }

    private void LoadFrom(ProfileDto profile)
    {
        Username = profile.UserName;
        Email = profile.Email;
        Input = new InputModel
        {
            DisplayName = profile.DisplayName,
            PhoneNumber = profile.PhoneNumber
        };
    }
}

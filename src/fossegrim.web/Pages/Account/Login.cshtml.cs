using Fossegrim.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Web.Pages.Account;

public class LoginModel : PageModel
{
    private readonly FossegrimApiClient _apiClient;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(FossegrimApiClient apiClient, ILogger<LoginModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ModelState.AddModelError(string.Empty, ErrorMessage);
        }

        // TODO: remove this once we're past local dev - pre-fills the seeded default admin account.
        Input.Email = "admin@fossegrim.local";
        Input.Password = "Admin123!";

        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (ModelState.IsValid)
        {
            var result = await _apiClient.LoginAsync(Input.Email, Input.Password);

            if (result.Success)
            {
                _logger.LogInformation("User logged in.");
                await AuthCookieSignIn.SignInAsync(HttpContext, result.Response!, Input.RememberMe);
                return LocalRedirect(returnUrl);
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
        }

        return Page();
    }
}

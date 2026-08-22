using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fossegrim.Web.Pages;

public class PlayerModel : PageModel
{
    private readonly FossegrimDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;

    public PlayerModel(FossegrimDbContext db, UserManager<ApplicationUser> userManager, IConfiguration configuration)
    {
        _db = db;
        _userManager = userManager;
        _configuration = configuration;
    }

    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public string? StreamUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            ErrorMessage = "You must be logged in to play this track.";
            return;
        }

        var mediaItem = await _db.MediaItems.FindAsync(id);

        if (mediaItem is null || string.IsNullOrWhiteSpace(mediaItem.FileLocation) || !System.IO.File.Exists(mediaItem.FileLocation))
        {
            ErrorMessage = "Track not found.";
            return;
        }

        Title = mediaItem.Title;
        Artist = mediaItem.ArtistName;
        Album = mediaItem.Album;

        var roles = await _userManager.GetRolesAsync(user);
        var jwtSection = _configuration.GetSection("Jwt");
        var (token, _) = JwtTokenService.GenerateToken(
            user,
            roles,
            jwtSection["Issuer"]!,
            jwtSection["Audience"]!,
            jwtSection["Key"]!,
            jwtSection.GetValue<int>("ExpiryMinutes"));

        StreamUrl = $"/stream/{mediaItem.Id}?access_token={Uri.EscapeDataString(token)}";
    }
}

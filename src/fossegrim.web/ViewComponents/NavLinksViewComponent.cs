using Microsoft.AspNetCore.Mvc;

namespace Fossegrim.Web.ViewComponents;

public class NavLinksViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(bool centered = false)
    {
        var currentPath = HttpContext.Request.Path.ToString();
        var links = GetNavigationLinks(currentPath);

        var model = new NavLinksViewModel
        {
            Centered = centered,
            Links = links
        };

        return View(model);
    }

    private List<NavLink> GetNavigationLinks(string currentPath)
    {
        // Account pages
        if (currentPath.StartsWith("/Account/Login"))
        {
            return new List<NavLink>
            {
                new NavLink("/Account/Register", "Create account"),
                new NavLink("/", "Home")
            };
        }

        if (currentPath.StartsWith("/Account/Register"))
        {
            return new List<NavLink>
            {
                new NavLink("/Account/Login", "Already have an account?"),
                new NavLink("/", "Home")
            };
        }

        if (currentPath.StartsWith("/Account/Logout"))
        {
            return new List<NavLink>
            {
                new NavLink("/", "Home")
            };
        }

        if (currentPath.StartsWith("/Account/ChangePassword"))
        {
            return new List<NavLink>
            {
                new NavLink("/Account/Profile", "Back to Profile"),
                new NavLink("/", "Home")
            };
        }

        if (currentPath.StartsWith("/Account/Profile"))
        {
            return new List<NavLink>
            {
                new NavLink("/Account/ChangePassword", "Change Password"),
                new NavLink("/", "Home"),
                new NavLink("/MediaLibrary", "Media Library")
            };
        }

        if (currentPath.StartsWith("/Account/AccessDenied"))
        {
            return new List<NavLink>
            {
                new NavLink("/Account/Login", "Login"),
                new NavLink("/", "Home")
            };
        }

        // Settings pages
        if (currentPath.StartsWith("/Settings/Settings"))
        {
            return new List<NavLink>
            {
                new NavLink("/Settings/Admin", "Admin Panel"),
                new NavLink("/MediaLibrary", "Media Library"),
                new NavLink("/", "Home")
            };
        }

        if (currentPath.StartsWith("/Settings/Admin"))
        {
            return new List<NavLink>
            {
                new NavLink("/MediaLibrary", "View Media Library"),
                new NavLink("/", "Home")
            };
        }

        // Default navigation
        return new List<NavLink>
        {
            new NavLink("/MediaLibrary", "Media Library"),
            new NavLink("/Settings/Admin", "Admin"),
            new NavLink("/", "Home")
        };
    }
}

public class NavLinksViewModel
{
    public bool Centered { get; set; }
    public List<NavLink> Links { get; set; } = new();
}

public class NavLink
{
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    public NavLink(string url, string text)
    {
        Url = url;
        Text = text;
    }
}

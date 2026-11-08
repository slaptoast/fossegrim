namespace Fossegrim.Web.Services;

/// <summary>Short "how long ago" text, e.g. "just now", "5 min ago", "3 days ago", "Mar 2024".</summary>
public static class RelativeTime
{
    public static string Format(DateTime utc, DateTime nowUtc)
    {
        var ago = nowUtc - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (ago < TimeSpan.FromHours(1))
        {
            return $"{(int)ago.TotalMinutes} min ago";
        }
        if (ago < TimeSpan.FromDays(1))
        {
            var hours = (int)ago.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }
        if (ago < TimeSpan.FromDays(60))
        {
            var days = (int)ago.TotalDays;
            return days == 1 ? "yesterday" : $"{days} days ago";
        }
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("MMM yyyy");
    }
}

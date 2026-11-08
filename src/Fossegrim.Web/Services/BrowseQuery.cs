using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// A request for one page of a library list (see LibrarySorts). Null Sort and
/// Order use the Api's defaults; Listened leaves out what the user has never
/// played, and Favorites keeps only their favourites.
/// </summary>
public record BrowseQuery(string? Q = null, string? Sort = null, string? Order = null, int Page = 1, int PageSize = 50, bool Listened = false, bool Favorites = false)
{
    public const int MaxPageSize = 500;

    public string ToQueryString()
    {
        var parts = new List<string> { $"page={Page}", $"pageSize={PageSize}" };
        if (!string.IsNullOrWhiteSpace(Q))
        {
            parts.Add($"q={Uri.EscapeDataString(Q.Trim())}");
        }
        if (!string.IsNullOrEmpty(Sort))
        {
            parts.Add($"sort={Sort}");
        }
        if (!string.IsNullOrEmpty(Order))
        {
            parts.Add($"order={Order}");
        }
        if (Listened)
        {
            parts.Add("listened=true");
        }
        if (Favorites)
        {
            parts.Add("favorites=true");
        }
        return "?" + string.Join('&', parts);
    }
}

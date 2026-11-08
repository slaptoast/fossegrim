namespace Fossegrim.Web.Services;

/// <summary>How a page's saved module layout and its modules' defaults combine.</summary>
public static class ModuleLayout
{
    /// <summary>
    /// The saved layout in module order, without modules that no longer exist.
    /// With nothing saved, it's the defaults. A module the saved layout doesn't
    /// mention (one added since it was saved) goes at the top, so it's seen,
    /// and everything else moves down to make room; several new ones stack in
    /// module order. One that starts hidden stays hidden where its default is.
    /// </summary>
    public static List<LayoutItem> Merge(IReadOnlyList<LayoutItem> defaults, IReadOnlyList<LayoutItem>? saved)
    {
        if (saved is not { Count: > 0 })
        {
            return defaults.Select(Copy).ToList();
        }

        var added = defaults.Where(d => !d.Hidden && saved.All(s => s.Id != d.Id)).ToList();
        var shift = added.Sum(d => d.H);
        var top = 0;
        var layout = new List<LayoutItem>(defaults.Count);
        foreach (var module in defaults)
        {
            LayoutItem item;
            if (saved.FirstOrDefault(s => s.Id == module.Id) is { } kept)
            {
                item = Copy(kept);
                item.Y += shift;
            }
            else
            {
                item = Copy(module);
                if (!item.Hidden)
                {
                    item.Y = top;
                    top += item.H;
                }
            }
            layout.Add(item);
        }
        return layout;
    }

    public static LayoutItem Copy(LayoutItem item) => new()
    {
        Id = item.Id, X = item.X, Y = item.Y, W = item.W, H = item.H, Hidden = item.Hidden
    };
}

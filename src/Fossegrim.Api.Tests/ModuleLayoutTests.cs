using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

// The web app's merging of a saved module layout with the modules' defaults.
public class ModuleLayoutTests
{
    private static LayoutItem Item(string id, int x, int y, int w, int h, bool hidden = false) =>
        new() { Id = id, X = x, Y = y, W = w, H = h, Hidden = hidden };

    private static string Describe(IEnumerable<LayoutItem> layout) =>
        string.Join(" ", layout.Select(i => $"{i.Id}:{i.X},{i.Y},{i.W},{i.H}{(i.Hidden ? ",hidden" : "")}"));

    [Fact]
    public void NothingSaved_UsesTheDefaults()
    {
        List<LayoutItem> defaults = [Item("a", 0, 0, 8, 10), Item("b", 0, 10, 24, 12)];

        Assert.Equal("a:0,0,8,10 b:0,10,24,12", Describe(ModuleLayout.Merge(defaults, null)));
        Assert.Equal("a:0,0,8,10 b:0,10,24,12", Describe(ModuleLayout.Merge(defaults, [])));
    }

    [Fact]
    public void ANewModule_GoesAtTheTop_AndTheRestMoveDown()
    {
        List<LayoutItem> defaults = [Item("a", 0, 0, 8, 10), Item("b", 8, 0, 8, 10), Item("new", 4, 40, 24, 12)];
        List<LayoutItem> saved = [Item("b", 0, 0, 12, 6), Item("a", 0, 6, 12, 4)];

        Assert.Equal("a:0,18,12,4 b:0,12,12,6 new:4,0,24,12", Describe(ModuleLayout.Merge(defaults, saved)));
    }

    [Fact]
    public void SeveralNewModules_StackInModuleOrder()
    {
        List<LayoutItem> defaults = [Item("a", 0, 0, 24, 10), Item("one", 0, 20, 8, 5), Item("two", 8, 20, 8, 7)];
        List<LayoutItem> saved = [Item("a", 0, 0, 24, 10)];

        Assert.Equal("a:0,12,24,10 one:0,0,8,5 two:8,5,8,7", Describe(ModuleLayout.Merge(defaults, saved)));
    }

    [Fact]
    public void ANewModuleThatStartsHidden_StaysOutOfTheWay()
    {
        List<LayoutItem> defaults = [Item("a", 0, 0, 24, 10), Item("player", 16, 32, 8, 10, hidden: true)];
        List<LayoutItem> saved = [Item("a", 0, 3, 24, 10)];

        Assert.Equal("a:0,3,24,10 player:16,32,8,10,hidden", Describe(ModuleLayout.Merge(defaults, saved)));
    }

    [Fact]
    public void RemovedModules_AreDropped_AndTheSavedLayoutIsntChanged()
    {
        List<LayoutItem> defaults = [Item("a", 0, 0, 24, 10), Item("new", 0, 30, 24, 4)];
        List<LayoutItem> saved = [Item("gone", 0, 0, 8, 8), Item("a", 0, 8, 24, 10)];

        var layout = ModuleLayout.Merge(defaults, saved);

        Assert.Equal("a:0,12,24,10 new:0,0,24,4", Describe(layout));
        Assert.Equal("gone:0,0,8,8 a:0,8,24,10", Describe(saved));
    }
}

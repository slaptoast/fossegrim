using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

// The web app's FavoritesService, against the real Api.
public class FavoritesServiceTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public FavoritesServiceTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Toggling_SavesTheFavourite_AndRaisesChanged()
    {
        var user = await _factory.CreateUserAsync();
        var track = await _factory.AddMediaItemAsync();
        var (favorites, _) = Create(user);
        await favorites.EnsureLoadedAsync();
        var changed = new List<string>();
        favorites.Changed += changed.Add;

        await favorites.ToggleAsync(FavoriteKinds.Tracks, track.Id);

        Assert.True(favorites.IsFavorite(FavoriteKinds.Tracks, track.Id));
        Assert.Equal([FavoriteKinds.Tracks], changed);
        Assert.Equal([track.Id], (await user.Client.GetFromJsonAsync<FavoritesDto>("/api/favorites"))!.TrackIds);

        // A fresh page load sees it too.
        var (reloaded, _) = Create(user);
        await reloaded.EnsureLoadedAsync();
        Assert.True(reloaded.IsFavorite(FavoriteKinds.Tracks, track.Id));

        await favorites.ToggleAsync(FavoriteKinds.Tracks, track.Id);
        Assert.Empty((await user.Client.GetFromJsonAsync<FavoritesDto>("/api/favorites"))!.TrackIds);
    }

    [Fact]
    public async Task ARefusedChange_IsUndone_WithAToast()
    {
        var user = await _factory.CreateUserAsync();
        var (favorites, toasts) = Create(user);
        await favorites.EnsureLoadedAsync();
        var missing = Guid.NewGuid();

        await favorites.ToggleAsync(FavoriteKinds.Albums, missing);

        Assert.False(favorites.IsFavorite(FavoriteKinds.Albums, missing));
        Assert.StartsWith("Couldn't add to favourites", Assert.Single(toasts));
    }

    private static (FavoritesService Service, List<string> Toasts) Create(TestUser user)
    {
        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var toasts = new List<string>();
        var toastService = new ToastService();
        toastService.Shown += toasts.Add;
        return (new FavoritesService(WebClients.Create(user.Client, new TokenStore(new QueueTests.FakeLocalStorage(token))), toastService), toasts);
    }
}

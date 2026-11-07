using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// The signed-in user's favourites, loaded once and shared by every heart
/// on the page. A heart changes at once; if the Api then refuses, it changes
/// back and a toast says so.
/// </summary>
public class FavoritesService
{
    private readonly FossegrimApiClient _apiClient;
    private readonly ToastService _toasts;
    private readonly Dictionary<string, HashSet<Guid>> _ids = new()
    {
        [FavoriteKinds.Tracks] = [],
        [FavoriteKinds.Albums] = [],
        [FavoriteKinds.Artists] = [],
    };
    private Task? _loading;

    public FavoritesService(FossegrimApiClient apiClient, ToastService toasts)
    {
        _apiClient = apiClient;
        _toasts = toasts;
    }

    /// <summary>Raised after a favourite is added or removed (and after loading), with its kind.</summary>
    public event Action<string>? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public bool IsFavorite(string kind, Guid id) => _ids[kind].Contains(id);

    public Task ToggleAsync(string kind, Guid id) => SetAsync(kind, id, !IsFavorite(kind, id));

    public async Task SetAsync(string kind, Guid id, bool favorite)
    {
        if (IsFavorite(kind, id) == favorite)
        {
            return;
        }

        Apply(kind, id, favorite);
        try
        {
            await _apiClient.SetFavoriteAsync(kind, id, favorite);
        }
        catch (Exception ex)
        {
            Apply(kind, id, !favorite);
            _toasts.Show(favorite ? $"Couldn't add to favourites: {ex.Message}" : $"Couldn't remove from favourites: {ex.Message}");
        }
    }

    private void Apply(string kind, Guid id, bool favorite)
    {
        if (favorite)
        {
            _ids[kind].Add(id);
        }
        else
        {
            _ids[kind].Remove(id);
        }
        Changed?.Invoke(kind);
    }

    private async Task LoadAsync()
    {
        try
        {
            var favorites = await _apiClient.GetFavoritesAsync();
            _ids[FavoriteKinds.Tracks].UnionWith(favorites.TrackIds);
            _ids[FavoriteKinds.Albums].UnionWith(favorites.AlbumIds);
            _ids[FavoriteKinds.Artists].UnionWith(favorites.ArtistIds);
            Changed?.Invoke(FavoriteKinds.Tracks);
            Changed?.Invoke(FavoriteKinds.Albums);
            Changed?.Invoke(FavoriteKinds.Artists);
        }
        catch
        {
            // Hearts show as empty; try again on the next page that needs them.
            _loading = null;
        }
    }
}

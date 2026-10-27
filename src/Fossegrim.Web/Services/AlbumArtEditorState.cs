namespace Fossegrim.Web.Services;

// The album being edited, as known where the editor was opened from.
public record AlbumArtTarget(Guid AlbumId, string AlbumName, string? ArtistName);

/// <summary>
/// Which album the album art editor is showing, shared by the right-click
/// menus that open it and the editors that show it (the temporary panel at
/// the top of the Library, or the module someone added to their layout).
/// Also announces saved covers, so album lists can reload the image.
/// </summary>
public class AlbumArtEditorState
{
    private readonly Dictionary<Guid, int> _coverVersions = [];

    public AlbumArtTarget? Target { get; private set; }

    // Set by the Library page: the editor only exists there, so the menus
    // elsewhere don't offer it.
    public bool IsAvailable { get; set; }

    public event Action? Changed;

    public event Action<Guid>? CoverChanged;

    public void Open(AlbumArtTarget target)
    {
        Target = target;
        Changed?.Invoke();
    }

    public void Close()
    {
        Target = null;
        Changed?.Invoke();
    }

    /// <summary>Changes after each save, for cache-busting the album's cover URL.</summary>
    public int CoverVersion(Guid albumId) => _coverVersions.GetValueOrDefault(albumId);

    public void NotifyCoverChanged(Guid albumId)
    {
        _coverVersions[albumId] = CoverVersion(albumId) + 1;
        CoverChanged?.Invoke(albumId);
    }
}

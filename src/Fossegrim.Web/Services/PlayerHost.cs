using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// Connects the player engine (PlayerEngine, always in the app layout) with
/// the places the player is shown: the floating player bar, and the Player
/// panel the Library page's layout can include. The bar steps aside while a
/// panel is on screen, so there's one set of controls at a time.
/// </summary>
public class PlayerHost
{
    private TaskCompletionSource<IJSObjectReference> _player = NewPlayerSource();
    private Func<Task>? _previous;
    private int _panels;

    /// <summary>Raised when a Player panel appears or goes.</summary>
    public event Action? Changed;

    /// <summary>Whether a Player panel is on screen (so the bar is hidden).</summary>
    public bool PanelShown => _panels > 0;

    public void PanelAdded()
    {
        _panels++;
        Changed?.Invoke();
    }

    public void PanelRemoved()
    {
        _panels = Math.Max(0, _panels - 1);
        Changed?.Invoke();
    }

    /// <summary>
    /// Mounts play/seek/volume controls into <paramref name="host"/> (an
    /// element Blazor renders empty), once the engine is ready. Dispose the
    /// result's JS object with "dispose" when the element goes.
    /// </summary>
    public async Task<IJSObjectReference> MountControlsAsync(ElementReference host)
    {
        var player = await _player.Task;
        return await player.InvokeAsync<IJSObjectReference>("mountControls", host);
    }

    /// <summary>Previous: restarts the track past its first seconds, otherwise goes back one.</summary>
    public Task PreviousAsync() => _previous?.Invoke() ?? Task.CompletedTask;

    // ---- For PlayerEngine ----

    internal void EngineStarted(IJSObjectReference player, Func<Task> previous)
    {
        _previous = previous;
        _player.TrySetResult(player);
    }

    // Signing out removes the layout and its engine; the next sign-in starts a new one.
    internal void EngineStopped()
    {
        _previous = null;
        if (_player.Task.IsCompleted)
        {
            _player = NewPlayerSource();
        }
    }

    private static TaskCompletionSource<IJSObjectReference> NewPlayerSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

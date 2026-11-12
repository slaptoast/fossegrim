using System.Text.Json;
using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// The equalizer shared by the Library module and the Profile page: loads
/// the settings saved on this device, saves each change there, and hands
/// the curve to the audio graph (audioGraph.js) straight away.
/// </summary>
public sealed class EqualizerService
{
    private const string StorageKey = "fossegrim.equalizer";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _js;
    private IJSObjectReference? _graph;
    private Task? _loading;

    public EqualizerService(IJSRuntime js)
    {
        _js = js;
    }

    public EqualizerSettings Current { get; private set; } = new();

    public event Action? Changed;

    /// <summary>Loads this device's settings once and applies them. Safe to call repeatedly.</summary>
    public Task InitializeAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        EqualizerSettings? stored = null;
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            stored = json is null ? null : JsonSerializer.Deserialize<EqualizerSettings>(json, Json);
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            // Storage blocked or unreadable: start flat and off.
        }
        Current = EqualizerSettings.Normalize(stored);
        await ApplyAsync();
        Changed?.Invoke();
    }

    public Task SetEnabledAsync(bool enabled) => UpdateAsync(s => s.Enabled = enabled);

    public Task SelectPresetAsync(string preset) => UpdateAsync(s =>
    {
        if (preset == EqualizerPresets.Custom || EqualizerPresets.Find(preset) is not null)
        {
            s.Preset = preset;
        }
    });

    /// <summary>Moving a slider starts a custom curve from what was showing.</summary>
    public Task SetBandAsync(int band, double gain) => UpdateAsync(s =>
    {
        if (s.Preset != EqualizerPresets.Custom)
        {
            s.CustomGains = s.Gains.ToArray();
            s.Preset = EqualizerPresets.Custom;
        }
        s.CustomGains[band] = EqualizerPresets.Clamp(gain);
    });

    private async Task UpdateAsync(Action<EqualizerSettings> change)
    {
        await InitializeAsync();
        change(Current);
        Current = EqualizerSettings.Normalize(Current);
        Changed?.Invoke();
        await ApplyAsync();
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(Current, Json));
        }
        catch (JSException)
        {
            // Storage blocked: the change still applies until the page reloads.
        }
    }

    private async Task ApplyAsync()
    {
        try
        {
            _graph ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/audioGraph.js");
            var gains = Current.Enabled ? Current.Gains.ToArray() : EqualizerPresets.FlatGains;
            await _graph.InvokeVoidAsync("setEqualizer", Current.Enabled, gains, EqualizerSettings.PreampFor(gains));
        }
        catch (JSException)
        {
            // No Web Audio: playback carries on without the equalizer.
        }
    }
}

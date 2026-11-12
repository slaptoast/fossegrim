using System.Text.Json.Serialization;

namespace Fossegrim.Web.Services;

/// <summary>
/// The equalizer's settings, kept per device (localStorage) rather than with
/// the account: headphones and speakers usually want different curves.
/// Preset is a preset's id or <see cref="EqualizerPresets.Custom"/>; the custom
/// curve is kept while a preset is chosen, so switching back restores it.
/// </summary>
public sealed class EqualizerSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = EqualizerPresets.Flat;
    public double[] CustomGains { get; set; } = new double[EqualizerPresets.Bands.Count];

    /// <summary>The gain for each band in dB, from the preset or the custom curve.</summary>
    [JsonIgnore]
    public IReadOnlyList<double> Gains =>
        Preset == EqualizerPresets.Custom ? CustomGains : EqualizerPresets.Find(Preset)?.Gains ?? EqualizerPresets.FlatGains;

    /// <summary>
    /// Settings as read back from storage, made safe: unknown presets become
    /// Flat, and the custom curve gets exactly one gain per band, in range.
    /// </summary>
    public static EqualizerSettings Normalize(EqualizerSettings? stored)
    {
        var settings = stored ?? new EqualizerSettings();
        var gains = new double[EqualizerPresets.Bands.Count];
        for (var i = 0; i < gains.Length; i++)
        {
            gains[i] = EqualizerPresets.Clamp(settings.CustomGains is { } custom && i < custom.Length ? custom[i] : 0);
        }
        return new EqualizerSettings
        {
            Enabled = settings.Enabled,
            Preset = settings.Preset == EqualizerPresets.Custom || EqualizerPresets.Find(settings.Preset) is not null
                ? settings.Preset
                : EqualizerPresets.Flat,
            CustomGains = gains,
        };
    }

    /// <summary>
    /// Boosting can push the signal past full scale, which distorts; the
    /// level goes down by the biggest boost first so the loudest band peaks
    /// where it did.
    /// </summary>
    public static double PreampFor(IReadOnlyList<double> gains) => -Math.Max(0, gains.DefaultIfEmpty(0).Max());
}

public sealed record EqualizerPreset(string Id, string Label, double[] Gains);

public static class EqualizerPresets
{
    public const double MaxGain = 12;
    public const string Custom = "custom";
    public const string Flat = "flat";

    /// <summary>Centre frequencies of the ten bands, in Hz (a classic ISO octave graphic EQ).</summary>
    public static readonly IReadOnlyList<double> Bands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly double[] FlatGains = new double[10];

    public static readonly IReadOnlyList<EqualizerPreset> All =
    [
        new(Flat, "Flat", FlatGains),
        new("bass", "Bass boost", [6, 5, 4, 2, 0, 0, 0, 0, 0, 0]),
        new("treble", "Treble boost", [0, 0, 0, 0, 0, 1, 2, 4, 5, 6]),
        new("vocal", "Vocal", [-2, -2, -1, 1, 3, 3, 2, 1, 0, -1]),
        new("rock", "Rock", [4, 3, 2, 0, -1, -1, 1, 2, 3, 4]),
        new("electronic", "Electronic", [5, 4, 1, 0, -2, 1, 0, 1, 4, 5]),
        new("classical", "Classical", [0, 0, 0, 0, 0, 0, -2, -3, -3, -4]),
        new("loudness", "Loudness", [6, 4, 2, 0, 0, 0, 0, 1, 3, 4]),
    ];

    public static EqualizerPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static double Clamp(double gain) =>
        double.IsFinite(gain) ? Math.Clamp(Math.Round(gain, 1), -MaxGain, MaxGain) : 0;

    /// <summary>"31", "500", "1k", "16k".</summary>
    public static string Label(double hz) => hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}";
}

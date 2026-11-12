using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

public class EqualizerTests
{
    [Fact]
    public void Presets_HaveAGainPerBand_WithinRange_AndUniqueIds()
    {
        Assert.Equal(10, EqualizerPresets.Bands.Count);
        Assert.All(EqualizerPresets.All, p =>
        {
            Assert.Equal(EqualizerPresets.Bands.Count, p.Gains.Length);
            Assert.All(p.Gains, g => Assert.InRange(g, -EqualizerPresets.MaxGain, EqualizerPresets.MaxGain));
        });
        Assert.Equal(EqualizerPresets.All.Count, EqualizerPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(EqualizerPresets.All, p => p.Id == EqualizerPresets.Custom);
    }

    [Fact]
    public void Normalize_StartsFlatAndOff_WithNothingStored()
    {
        var settings = EqualizerSettings.Normalize(null);

        Assert.False(settings.Enabled);
        Assert.Equal(EqualizerPresets.Flat, settings.Preset);
        Assert.All(settings.Gains, g => Assert.Equal(0, g));
    }

    [Fact]
    public void Normalize_RepairsWhatStorageGaveBack()
    {
        var settings = EqualizerSettings.Normalize(new EqualizerSettings
        {
            Enabled = true,
            Preset = "no-such-preset",
            CustomGains = [40, -40, double.NaN, 3.04],
        });

        Assert.True(settings.Enabled);
        Assert.Equal(EqualizerPresets.Flat, settings.Preset);
        Assert.Equal([12, -12, 0, 3, 0, 0, 0, 0, 0, 0], settings.CustomGains);
    }

    [Fact]
    public void Gains_ComeFromThePreset_OrTheCustomCurve_WhichIsKept()
    {
        var custom = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var settings = new EqualizerSettings { Preset = "bass", CustomGains = custom };

        Assert.Equal(EqualizerPresets.Find("bass")!.Gains, settings.Gains);
        settings.Preset = EqualizerPresets.Custom;
        Assert.Equal(custom, settings.Gains);
    }

    [Theory]
    [InlineData(new double[] { 0, 0, 0 }, 0)]
    [InlineData(new double[] { -6, -3, 0 }, 0)]
    [InlineData(new double[] { 6, 2, -4 }, -6)]
    public void Preamp_TakesOffTheBiggestBoost(double[] gains, double expected)
    {
        Assert.Equal(expected, EqualizerSettings.PreampFor(gains));
    }

    [Theory]
    [InlineData(31, "31")]
    [InlineData(500, "500")]
    [InlineData(1000, "1k")]
    [InlineData(16000, "16k")]
    public void BandLabels_AreShort(double hz, string expected)
    {
        Assert.Equal(expected, EqualizerPresets.Label(hz));
    }
}

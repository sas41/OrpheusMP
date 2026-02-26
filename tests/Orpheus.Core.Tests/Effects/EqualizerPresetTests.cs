using Orpheus.Core.Effects;

namespace Orpheus.Core.Tests.Effects;

public class EqualizerPresetTests
{
    [Fact]
    public void All_ContainsExpectedPresets()
    {
        Assert.True(EqualizerPresets.All.Count >= 12);
    }

    [Fact]
    public void Flat_HasAllZeroGains()
    {
        var flat = EqualizerPresets.Flat;
        Assert.Equal(10, flat.BandGains.Length);
        Assert.All(flat.BandGains, gain => Assert.Equal(0f, gain));
        Assert.Equal(0f, flat.Preamp);
    }

    [Fact]
    public void AllPresets_Have10Bands()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            Assert.Equal(10, preset.BandGains.Length);
        }
    }

    [Fact]
    public void AllPresets_HaveNames()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
        }
    }

    [Fact]
    public void GetByName_FindsExistingPreset()
    {
        var rock = EqualizerPresets.GetByName("Rock");
        Assert.NotNull(rock);
        Assert.Equal("Rock", rock.Name);
    }

    [Fact]
    public void GetByName_IsCaseInsensitive()
    {
        var jazz = EqualizerPresets.GetByName("jazz");
        Assert.NotNull(jazz);
        Assert.Equal("Jazz", jazz.Name);
    }

    [Fact]
    public void GetByName_ReturnsNullForUnknown()
    {
        var result = EqualizerPresets.GetByName("NonExistent");
        Assert.Null(result);
    }

    [Fact]
    public void EqualizerBand_FrequencyLabel_FormatsCorrectly()
    {
        var lowBand = new EqualizerBand(0, 31.25f);
        Assert.Equal("31 Hz", lowBand.FrequencyLabel);

        var midBand = new EqualizerBand(5, 1000f);
        Assert.Equal("1 kHz", midBand.FrequencyLabel);

        var highBand = new EqualizerBand(9, 16000f);
        Assert.Equal("16 kHz", highBand.FrequencyLabel);
    }

    [Fact]
    public void EqualizerBand_ToString_IncludesGain()
    {
        var band = new EqualizerBand(0, 1000f, 3.5f);
        var str = band.ToString();

        Assert.Contains("1 kHz", str);
        Assert.Contains("+3.5", str);
        Assert.Contains("dB", str);
    }

    [Fact]
    public void EqualizerBand_Gain_CanBeModified()
    {
        var band = new EqualizerBand(0, 1000f);
        Assert.Equal(0f, band.Gain);

        band.Gain = 5.5f;
        Assert.Equal(5.5f, band.Gain);
    }

    [Fact]
    public void Rock_HasBassAndTrebleBoost()
    {
        var rock = EqualizerPresets.Rock;
        // Rock typically boosts bass (bands 0-1) and treble (bands 7-9).
        Assert.True(rock.BandGains[0] > 0); // 31 Hz boosted.
        Assert.True(rock.BandGains[9] > 0); // 16 kHz boosted.
    }

    [Fact]
    public void Preset_ToString_ReturnsName()
    {
        Assert.Equal("Rock", EqualizerPresets.Rock.ToString());
        Assert.Equal("Jazz", EqualizerPresets.Jazz.ToString());
    }
}

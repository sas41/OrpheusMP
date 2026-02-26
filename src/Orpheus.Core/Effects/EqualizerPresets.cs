namespace Orpheus.Core.Effects;

/// <summary>
/// Built-in equalizer presets. Band order matches the standard 10-band EQ:
/// 31 Hz, 63 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz, 2 kHz, 4 kHz, 8 kHz, 16 kHz.
/// </summary>
public static class EqualizerPresets
{
    public static EqualizerPreset Flat { get; } = new()
    {
        Name = "Flat",
        Preamp = 0f,
        BandGains = [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]
    };

    public static EqualizerPreset Rock { get; } = new()
    {
        Name = "Rock",
        Preamp = 0f,
        BandGains = [5.0f, 4.0f, -3.0f, -5.0f, -2.0f, 2.0f, 5.0f, 7.0f, 8.0f, 8.0f]
    };

    public static EqualizerPreset Pop { get; } = new()
    {
        Name = "Pop",
        Preamp = 0f,
        BandGains = [-1.5f, 3.0f, 5.0f, 6.0f, 4.0f, 0f, -1.0f, -1.5f, -1.5f, -1.5f]
    };

    public static EqualizerPreset Jazz { get; } = new()
    {
        Name = "Jazz",
        Preamp = 0f,
        BandGains = [4.0f, 3.0f, 1.0f, 2.0f, -1.5f, -1.5f, 0f, 1.5f, 3.0f, 4.0f]
    };

    public static EqualizerPreset Classical { get; } = new()
    {
        Name = "Classical",
        Preamp = 0f,
        BandGains = [5.0f, 4.0f, 3.0f, 2.5f, -1.5f, -1.5f, 0f, 2.0f, 3.0f, 4.0f]
    };

    public static EqualizerPreset Electronic { get; } = new()
    {
        Name = "Electronic",
        Preamp = 0f,
        BandGains = [6.0f, 5.0f, 1.0f, 0f, -2.0f, 2.0f, 0.5f, 1.0f, 5.0f, 6.0f]
    };

    public static EqualizerPreset HipHop { get; } = new()
    {
        Name = "Hip-Hop",
        Preamp = 0f,
        BandGains = [6.0f, 5.0f, 1.5f, 3.0f, -1.0f, -1.0f, 1.5f, -0.5f, 2.0f, 3.0f]
    };

    public static EqualizerPreset Vocal { get; } = new()
    {
        Name = "Vocal",
        Preamp = 0f,
        BandGains = [-2.0f, -3.0f, -3.0f, 1.5f, 4.0f, 4.0f, 3.5f, 1.5f, 0f, -2.0f]
    };

    public static EqualizerPreset BassBoost { get; } = new()
    {
        Name = "Bass Boost",
        Preamp = -3f,
        BandGains = [8.0f, 6.0f, 4.0f, 1.0f, 0f, 0f, 0f, 0f, 0f, 0f]
    };

    public static EqualizerPreset TrebleBoost { get; } = new()
    {
        Name = "Treble Boost",
        Preamp = -3f,
        BandGains = [0f, 0f, 0f, 0f, 0f, 1.0f, 3.0f, 5.0f, 7.0f, 9.0f]
    };

    public static EqualizerPreset Loudness { get; } = new()
    {
        Name = "Loudness",
        Preamp = -3f,
        BandGains = [6.0f, 4.0f, 0f, 0f, -2.0f, 0f, -1.0f, -5.0f, 5.0f, 1.0f]
    };

    public static EqualizerPreset Headphones { get; } = new()
    {
        Name = "Headphones",
        Preamp = 0f,
        BandGains = [3.0f, 7.0f, 3.5f, -1.0f, -2.5f, 1.0f, 3.0f, 6.0f, 9.0f, 10.0f]
    };

    /// <summary>
    /// All built-in presets.
    /// </summary>
    public static IReadOnlyList<EqualizerPreset> All { get; } =
    [
        Flat, Rock, Pop, Jazz, Classical, Electronic,
        HipHop, Vocal, BassBoost, TrebleBoost, Loudness, Headphones
    ];

    /// <summary>
    /// Get a preset by name (case-insensitive). Returns null if not found.
    /// </summary>
    public static EqualizerPreset? GetByName(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

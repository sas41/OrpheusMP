namespace Orpheus.Core.Effects;

/// <summary>
/// A named equalizer preset with preamp and band gain values.
/// </summary>
public sealed class EqualizerPreset
{
    /// <summary>
    /// Name of the preset (e.g., "Flat", "Rock", "Jazz").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Preamp gain in dB.
    /// </summary>
    public float Preamp { get; init; }

    /// <summary>
    /// Gain values for each band, in order.
    /// </summary>
    public required float[] BandGains { get; init; }

    public override string ToString() => Name;
}

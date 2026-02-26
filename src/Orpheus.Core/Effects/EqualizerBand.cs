namespace Orpheus.Core.Effects;

/// <summary>
/// Represents a single frequency band in an equalizer.
/// </summary>
public sealed class EqualizerBand
{
    /// <summary>
    /// Index of this band in the equalizer.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Center frequency of this band in Hz.
    /// </summary>
    public float Frequency { get; }

    /// <summary>
    /// Gain/attenuation in dB. Typically ranges from -20 to +20.
    /// </summary>
    public float Gain { get; set; }

    public EqualizerBand(int index, float frequency, float gain = 0f)
    {
        Index = index;
        Frequency = frequency;
        Gain = gain;
    }

    /// <summary>
    /// Returns a human-readable label for the frequency (e.g., "1 kHz", "60 Hz").
    /// </summary>
    public string FrequencyLabel =>
        Frequency >= 1000
            ? $"{Frequency / 1000:0.#} kHz"
            : $"{Frequency:0} Hz";

    public override string ToString() => $"{FrequencyLabel}: {Gain:+0.0;-0.0;0.0} dB";
}

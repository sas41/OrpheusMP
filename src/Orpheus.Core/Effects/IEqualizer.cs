namespace Orpheus.Core.Effects;

/// <summary>
/// Audio equalizer abstraction. Backend-agnostic interface for EQ control.
/// </summary>
public interface IEqualizer
{
    /// <summary>
    /// Whether the equalizer is currently active.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Preamp gain in dB.
    /// </summary>
    float Preamp { get; set; }

    /// <summary>
    /// The equalizer bands. Typically 10 bands (31 Hz to 16 kHz).
    /// </summary>
    IReadOnlyList<EqualizerBand> Bands { get; }

    /// <summary>
    /// Set the gain for a specific band.
    /// </summary>
    void SetBandGain(int bandIndex, float gainDb);

    /// <summary>
    /// Apply a preset, setting all band gains and preamp.
    /// </summary>
    void ApplyPreset(EqualizerPreset preset);

    /// <summary>
    /// Reset all bands to flat (0 dB).
    /// </summary>
    void Reset();

    /// <summary>
    /// Fired when any EQ setting changes.
    /// </summary>
    event EventHandler? SettingsChanged;
}

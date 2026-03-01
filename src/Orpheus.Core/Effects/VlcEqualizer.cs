using LibVLCSharp;

namespace Orpheus.Core.Effects;

/// <summary>
/// IEqualizer implementation backed by LibVLC's built-in equalizer.
/// Must be associated with a VLC MediaPlayer to take effect.
/// </summary>
public sealed class VlcEqualizer : IEqualizer, IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly Equalizer _equalizer;
    private readonly List<EqualizerBand> _bands;
    private bool _isEnabled;
    private bool _disposed;

    /// <summary>
    /// Standard 10-band EQ center frequencies.
    /// </summary>
    private static readonly float[] BandFrequencies =
        [31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    public VlcEqualizer(MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        _mediaPlayer = mediaPlayer;
        _equalizer = new Equalizer();

        _bands = new List<EqualizerBand>(BandFrequencies.Length);
        for (var i = 0; i < BandFrequencies.Length; i++)
        {
            _bands.Add(new EqualizerBand(i, BandFrequencies[i]));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (_isEnabled)
                ApplyToPlayer();
            else
                _mediaPlayer.UnsetEqualizer();

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float Preamp
    {
        get => _equalizer.Preamp;
        set
        {
            _equalizer.SetPreamp(value);
            if (_isEnabled) ApplyToPlayer();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<EqualizerBand> Bands => _bands;

    public void SetBandGain(int bandIndex, float gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bandIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bandIndex, _bands.Count);

        _bands[bandIndex].Gain = gainDb;
        _equalizer.SetAmp(gainDb, (uint)bandIndex);
        if (_isEnabled) ApplyToPlayer();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyPreset(EqualizerPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (preset.BandGains.Length != _bands.Count)
            throw new ArgumentException(
                $"Preset has {preset.BandGains.Length} bands, expected {_bands.Count}.",
                nameof(preset));

        _equalizer.SetPreamp(preset.Preamp);

        for (var i = 0; i < _bands.Count; i++)
        {
            _bands[i].Gain = preset.BandGains[i];
            _equalizer.SetAmp(preset.BandGains[i], (uint)i);
        }

        if (_isEnabled) ApplyToPlayer();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        ApplyPreset(EqualizerPresets.Flat);
    }

    public event EventHandler? SettingsChanged;

    private void ApplyToPlayer()
    {
        _mediaPlayer.SetEqualizer(_equalizer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isEnabled)
            _mediaPlayer.UnsetEqualizer();

        _equalizer.Dispose();
    }
}

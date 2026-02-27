using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Orpheus.Core.Effects;
using Lang = Orpheus.Desktop.Lang;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orpheus.Desktop.Views;

public partial class EqualizerWindow : Window
{
    private readonly VlcEqualizer? _equalizer;
    private readonly List<Slider> _bandSliders = new();
    private readonly List<TextBlock> _bandLabels = new();
    private bool _suppressEvents;

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML compiler.
    /// Do not use at runtime — call <see cref="EqualizerWindow(VlcEqualizer)"/> instead.
    /// </summary>
    public EqualizerWindow()
    {
        InitializeComponent();
    }

    public EqualizerWindow(VlcEqualizer equalizer) : this()
    {
        _equalizer = equalizer ?? throw new ArgumentNullException(nameof(equalizer));
        BuildBandSliders();
        PopulatePresets();
        SyncFromEqualizer();

        App.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => App.LanguageChanged -= OnLanguageChanged;
    }

    private void BuildBandSliders()
    {
        if (_equalizer is null) return;
        var bands = _equalizer.Bands;
        var colDefs = new ColumnDefinitions();
        for (var i = 0; i < bands.Count; i++)
            colDefs.Add(new ColumnDefinition(1, GridUnitType.Star));
        BandsGrid.ColumnDefinitions = colDefs;

        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];

            // Frequency label (top)
            var freqLabel = new TextBlock
            {
                Text = band.FrequencyLabel,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 10,
                Classes = { "muted" }
            };
            Grid.SetRow(freqLabel, 0);
            Grid.SetColumn(freqLabel, i);
            BandsGrid.Children.Add(freqLabel);

            // Vertical slider
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -20,
                Maximum = 20,
                Value = band.Gain,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var bandIndex = i;
            slider.ValueChanged += (_, e) => OnBandChanged(bandIndex, e.NewValue);
            Grid.SetRow(slider, 1);
            Grid.SetColumn(slider, i);
            BandsGrid.Children.Add(slider);
            _bandSliders.Add(slider);

            // Gain label (bottom)
            var gainLabel = new TextBlock
            {
                Text = $"{band.Gain:+0.0;-0.0;0.0}",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 10,
                Classes = { "muted" }
            };
            Grid.SetRow(gainLabel, 2);
            Grid.SetColumn(gainLabel, i);
            BandsGrid.Children.Add(gainLabel);
            _bandLabels.Add(gainLabel);
        }
    }

    private void PopulatePresets()
    {
        // Build a list of localized preset wrappers for the ComboBox
        var items = new List<LocalizedPreset>();
        foreach (var preset in EqualizerPresets.All)
        {
            // Map preset Name to locale key: e.g. "Bass Boost" -> "PresetBassBoost"
            var key = "Preset" + preset.Name.Replace("-", "").Replace(" ", "");
            var displayName = Lang.Resources.ResourceManager.GetString(key, Lang.Resources.Culture) ?? preset.Name;
            items.Add(new LocalizedPreset(preset, displayName));
        }
        PresetCombo.ItemsSource = items;
        PresetCombo.SelectedIndex = 0; // Flat
    }

    private void SyncFromEqualizer()
    {
        if (_equalizer is null) return;
        _suppressEvents = true;
        EnabledCheck.IsChecked = _equalizer.IsEnabled;
        PreampSlider.Value = _equalizer.Preamp;
        PreampLabel.Text = string.Format(Lang.Resources.DecibelFmt, $"{_equalizer.Preamp:+0.0;-0.0;0.0}");

        for (var i = 0; i < _equalizer.Bands.Count && i < _bandSliders.Count; i++)
        {
            _bandSliders[i].Value = _equalizer.Bands[i].Gain;
            _bandLabels[i].Text = $"{_equalizer.Bands[i].Gain:+0.0;-0.0;0.0}";
        }
        _suppressEvents = false;
    }

    public void OnEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _equalizer is null) return;
        _equalizer.IsEnabled = EnabledCheck.IsChecked ?? false;
    }

    public void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _equalizer is null) return;
        if (PresetCombo.SelectedItem is LocalizedPreset locPreset)
        {
            _equalizer.ApplyPreset(locPreset.Preset);
            SyncFromEqualizer();
        }
    }

    public void OnPreampChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || _equalizer is null) return;
        _equalizer.Preamp = (float)e.NewValue;
        PreampLabel.Text = string.Format(Lang.Resources.DecibelFmt, $"{e.NewValue:+0.0;-0.0;0.0}");
    }

    private void OnBandChanged(int bandIndex, double newValue)
    {
        if (_suppressEvents || _equalizer is null) return;
        _equalizer.SetBandGain(bandIndex, (float)newValue);
        if (bandIndex < _bandLabels.Count)
            _bandLabels[bandIndex].Text = $"{newValue:+0.0;-0.0;0.0}";
    }

    private void OnLanguageChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Re-populate presets with new localized names, preserving selection
            var selectedIndex = PresetCombo.SelectedIndex;
            PopulatePresets();
            _suppressEvents = true;
            PresetCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            _suppressEvents = false;

            // Re-localize preamp dB label
            SyncFromEqualizer();
        });
    }

    public void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_equalizer is null) return;
        _equalizer.Reset();
        SyncFromEqualizer();
    }
}

/// <summary>
/// Wraps an <see cref="EqualizerPreset"/> with a localized display name for the ComboBox.
/// </summary>
internal sealed record LocalizedPreset(EqualizerPreset Preset, string DisplayName)
{
    public override string ToString() => DisplayName;
}

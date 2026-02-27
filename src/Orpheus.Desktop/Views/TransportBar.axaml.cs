using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;

namespace Orpheus.Desktop.Views;

public partial class TransportBar : UserControl
{
    private const double SeekStepSeconds = 5.0;
    private const double VolumeStep = 2.0;
    private bool _seekEngaged;

    public TransportBar()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Suppress player position updates while the user is dragging the slider
        if (PositionSlider is not null)
        {
            PositionSlider.AddHandler(PointerPressedEvent, OnPositionSliderPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            PositionSlider.AddHandler(PointerReleasedEvent, OnPositionSliderReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
    }

    /// <summary>
    /// Returns true only when the pointer is over the Slider's track or thumb.
    /// Clicks on surrounding empty space in the parent Grid cell are ignored.
    /// We check against the track's template part for precise hit detection,
    /// falling back to the Slider's own bounds if the template part isn't found.
    /// </summary>
    private bool IsPointerOverSlider(PointerEventArgs e)
    {
        if (PositionSlider is null)
            return false;

        // Try to find the track part inside the Slider template for a precise check.
        var track = PositionSlider.FindDescendantOfType<Track>();
        if (track is not null)
        {
            var point = e.GetPosition(track);
            return new Rect(track.Bounds.Size).Contains(point);
        }

        // Fallback: check against the Slider's own bounds.
        var sliderPoint = e.GetPosition(PositionSlider);
        return new Rect(PositionSlider.Bounds.Size).Contains(sliderPoint);
    }

    private void OnPositionSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (!IsPointerOverSlider(e))
        {
            e.Handled = true;
            return;
        }

        _seekEngaged = true;
        ViewModel.IsUserSeekingPosition = true;
    }

    private void OnPositionSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ViewModel is null || !_seekEngaged)
            return;

        _seekEngaged = false;

        if (PositionSlider is not null)
            ViewModel.PlaybackPosition = PositionSlider.Value;

        ViewModel.IsUserSeekingPosition = false;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public async void OnPlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.TogglePlayPauseAsync();
    }

    public async void OnPrevClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.PlayPreviousAsync();
    }

    public async void OnNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.PlayNextAsync();
    }

    public async void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.StopAsync();
    }

    public void OnShuffleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ToggleShuffle();
    }

    public void OnRepeatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.CycleRepeat();
        if (sender is ToggleButton toggle)
            toggle.IsChecked = ViewModel?.IsRepeatEnabled ?? false;
    }

    public void OnPositionWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is null)
            return;

        var delta = e.Delta.Y * SeekStepSeconds;
        var newPos = Math.Clamp(ViewModel.PlaybackPosition + delta, 0, ViewModel.PlaybackDuration);
        ViewModel.PlaybackPosition = newPos;
        e.Handled = true;
    }

    public void OnVolumeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is null)
            return;

        var delta = e.Delta.Y * VolumeStep;
        ViewModel.Volume = Math.Clamp(ViewModel.Volume + delta, 0, 100);
        e.Handled = true;
    }

    public void OnVolumeIconClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ToggleMute();
    }
}

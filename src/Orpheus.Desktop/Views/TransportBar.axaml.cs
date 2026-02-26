using Avalonia.Controls;

namespace Orpheus.Desktop.Views;

public partial class TransportBar : UserControl
{
    public TransportBar()
    {
        InitializeComponent();
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

    public void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        _ = ViewModel.StopAsync();
    }

    public void OnShuffleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ToggleShuffle();
    }

    public void OnRepeatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.CycleRepeat();
    }
}

using Avalonia.Controls;

namespace Orpheus.Desktop.Views;

public partial class TrackListPanel : UserControl
{
    public TrackListPanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public async void OnTrackDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.PlaySelectedAsync();
    }
}

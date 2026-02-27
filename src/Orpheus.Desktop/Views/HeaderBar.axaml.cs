using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Orpheus.Desktop.Views;

public partial class HeaderBar : UserControl
{
    public HeaderBar()
    {
        InitializeComponent();
    }

    public void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var query = vm.SearchQuery;
        if (MainWindowViewModel.IsRadioUrl(query))
        {
            e.Handled = true;
            _ = vm.PlayRadioUrlAsync(query);
        }
    }

    public void OnEqualizerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var eq = vm.GetEqualizer();
        var window = new EqualizerWindow(eq);

        if (VisualRoot is Window parentWindow)
            window.ShowDialog(parentWindow);
        else
            window.Show();
    }

    public void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var mediaKeyService = (VisualRoot as MainWindow)?.MediaKeyService;
        var settingsVm = vm.CreateSettingsViewModel(mediaKeyService);

        // Suppress/restore global hotkeys while the user is rebinding a shortcut
        if (mediaKeyService is not null)
        {
            settingsVm.ShortcutListeningChanged += listening =>
                mediaKeyService.IsListening = listening;
        }

        var window = new SettingsWindow { DataContext = settingsVm };

        if (VisualRoot is Window parentWindow)
            window.ShowDialog(parentWindow);
        else
            window.Show();
    }
}

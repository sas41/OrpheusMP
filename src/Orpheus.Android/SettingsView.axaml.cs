using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Orpheus.Android;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Raised when the user taps the Back button so MainView can hide us.
    public event EventHandler? CloseRequested;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AddHandler(Button.ClickEvent, OnAnyButtonClick, handledEventsToo: false);
    }

    private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button btn) return;
        var vm = DataContext as MobileSettingsViewModel;

        switch (btn.Name)
        {
            case "SettingsBackButton":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "SettingsAddFolderButton":
                if (vm is not null) _ = PickAndAddFolderAsync(this, vm);
                break;

            case "SettingsRemoveFolderButton":
                if (vm is not null && btn.DataContext is string path)
                    _ = vm.RemoveFolderAsync(path);
                break;

            case "SettingsRescanButton":
                if (vm is not null) _ = vm.RescanAsync();
                break;

            case "SettingsResetLibraryButton":
                if (vm is not null) _ = vm.ResetLibraryAsync();
                break;

            case "LicenseToggleButton":
                if (btn.DataContext is MobileLicenseEntry entry)
                    entry.IsExpanded = !entry.IsExpanded;
                break;
        }
    }

    private static async System.Threading.Tasks.Task PickAndAddFolderAsync(
        Control anchor, MobileSettingsViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(anchor);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Add music folder", AllowMultiple = false });

        foreach (var folder in folders)
        {
            var path = MobileViewModel.ResolveStorageFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                await vm.AddFolderAsync(path);
        }
    }
}

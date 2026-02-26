using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;

namespace Orpheus.Desktop.Views;

public partial class LibraryPanel : UserControl
{
    public LibraryPanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public async void OnAddLibraryFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        var storage = topLevel?.StorageProvider;
        if (storage is null)
            return;

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add music folder",
            AllowMultiple = false
        });

        var picked = result.FirstOrDefault();
        var folder = picked?.Path?.LocalPath ?? picked?.Path?.ToString();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await ViewModel.AddLibraryFolderAsync(folder);
        }
    }

    public async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is TreeView tree && tree.SelectedItem is LibraryNode node)
        {
            await ViewModel.SelectFolderAsync(node.Path);
        }
    }

    public async void OnTreeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is TreeView tree && tree.SelectedItem is LibraryNode node)
        {
            await ViewModel.PlayFolderAsync(node.Path);
        }
    }
}

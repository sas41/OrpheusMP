using Avalonia.Controls;
using Avalonia.VisualTree;

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

        var dialog = new OpenFolderDialog
        {
            Title = "Add music folder"
        };

        var window = this.GetVisualRoot() as Window;
        if (window is null)
            return;

        var folder = await dialog.ShowAsync(window);
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

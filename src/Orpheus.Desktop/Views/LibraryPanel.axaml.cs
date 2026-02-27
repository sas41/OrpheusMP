using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Linq;

namespace Orpheus.Desktop.Views;

public partial class LibraryPanel : UserControl
{
    /// <summary>
    /// Custom double-click window (ms).  Looser than the typical 400–500 ms
    /// system default so that users don't have to click as fast.
    /// </summary>
    private const int DoubleClickMs = 650;
    private const double DragThreshold = 8;

    private DateTime _lastClickTime;
    private LibraryNode? _lastClickNode;
    private Point _dragStartPoint;
    private LibraryNode? _dragPendingNode;
    private bool _isManagedDragging;
    private IPointer? _capturedPointer;
    private TopLevel? _dragTopLevel;
    private TreeView? _tree;

    public LibraryPanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _tree = this.FindControl<TreeView>("LibraryTree");
        if (_tree is not null)
        {
            // Tunnel handler to intercept pointer presses before the TreeViewItem
            // expander receives them, so we can restrict expand/collapse to the arrow.
            _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
            _tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);

            // Note: the built-in TreeViewItem double-tap expand/collapse is
            // disabled by renaming PART_HeaderPresenter in our ControlTheme
            // so TreeViewItem.OnApplyTemplate can't find it to subscribe.
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPendingNode is null || _tree is null)
            return;

        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            _dragPendingNode = null;
            return;
        }

        var pos = e.GetPosition(_tree);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        var node = _dragPendingNode;
        _dragPendingNode = null;

        var label = node.Name;
        var payload = new System.Collections.Generic.Dictionary<string, string>
        {
            [DragFormats.LibraryNodePath] = node.Path,
            [DragFormats.LibraryNodeType] = ((int)node.NodeType).ToString(),
            [DragFormats.DragLabel] = label,
        };

        // Begin managed drag: capture pointer, show preview, notify drop targets
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        _dragTopLevel = topLevel;
        _isManagedDragging = true;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(_tree);

        topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        var previewText = new TextBlock
        {
            Text = label,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            Foreground = _tree.Foreground ?? Avalonia.Media.Brushes.White,
        };
        DragPreviewService.Show(previewText, topLevel, e.GetPosition(topLevel));
        ManagedDragService.Instance.Begin(topLevel, payload, e.GetPosition(topLevel));
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isManagedDragging || _dragTopLevel is null) return;

        var clientPos = e.GetPosition(_dragTopLevel);
        DragPreviewService.Move(_dragTopLevel, clientPos);
        ManagedDragService.Instance.Move(clientPos);
    }

    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isManagedDragging) return;
        EndManagedDrag();
    }

    private void EndManagedDrag()
    {
        _isManagedDragging = false;
        _dragPendingNode = null;

        ManagedDragService.Instance.End();
        DragPreviewService.Hide();

        _capturedPointer?.Capture(null);
        _capturedPointer = null;

        if (_dragTopLevel is not null)
        {
            _dragTopLevel.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
            _dragTopLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
            _dragTopLevel = null;
        }
    }

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

    public void OnToggleShowFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.ShowLibraryFiles = !ViewModel.ShowLibraryFiles;
    }

    public async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is TreeView tree && tree.SelectedItem is LibraryNode node
            && node.NodeType == LibraryNodeType.Folder)
        {
            await ViewModel.SelectFolderAsync(node.Path);
        }
    }

    /// <summary>
    /// Intercept pointer presses on the TreeView to implement:
    ///  - Expand/collapse ONLY via the chevron toggle button
    ///  - Custom double-click detection with a looser timer
    ///  - Double-click to play, but NOT when clicking the chevron
    /// </summary>
    private async void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || sender is not TreeView tree)
            return;

        var source = e.Source as Visual;
        if (source is null) return;

        var isOnChevron = IsOnExpandChevron(source);

        // Find the TreeViewItem that was clicked
        var treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem is null) return;

        // Find the LibraryNode for this item
        var node = treeViewItem.DataContext as LibraryNode;
        if (node is null) return;

        if (isOnChevron)
        {
            // Allow the chevron click to toggle expand/collapse (default behavior).
            // Reset double-click tracking so the chevron click doesn't count.
            _lastClickNode = null;
            _dragPendingNode = null;
            return;
        }

        // Not on chevron: prevent the TreeViewItem from toggling expand/collapse.
        // We do this by marking the event as handled so the TreeViewItem's
        // built-in toggle logic doesn't fire, then manually set selection.
        e.Handled = true;

        // Set selection manually since we handled the event
        tree.SelectedItem = node;

        // Track for potential drag
        _dragStartPoint = e.GetPosition(tree);
        _dragPendingNode = node;

        // Custom double-click detection
        var now = DateTime.UtcNow;
        if (_lastClickNode == node && (now - _lastClickTime).TotalMilliseconds <= DoubleClickMs)
        {
            // Double-click detected: dispatch based on node type
            _lastClickNode = null;
            _dragPendingNode = null;
            switch (node.NodeType)
            {
                case LibraryNodeType.Playlist:
                    await ViewModel.PlayPlaylistFileAsync(node.Path);
                    break;
                case LibraryNodeType.File:
                    await ViewModel.PlayFileAsync(node.Path);
                    break;
                default:
                    await ViewModel.PlayFolderAsync(node.Path);
                    break;
            }
        }
        else
        {
            _lastClickNode = node;
            _lastClickTime = now;
        }
    }

    /// <summary>
    /// Checks whether the given visual is inside (or is) the expand/collapse
    /// chevron toggle button (PART_ExpandCollapseChevron).
    /// </summary>
    private static bool IsOnExpandChevron(Visual visual)
    {
        Visual? current = visual;
        while (current is not null)
        {
            if (current is ToggleButton tb && tb.Name == "PART_ExpandCollapseChevron")
                return true;
            current = current.GetVisualParent() as Visual;
        }
        return false;
    }

    private static T? FindAncestor<T>(Visual visual) where T : Visual
    {
        Visual? current = visual;
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current.GetVisualParent() as Visual;
        }
        return null;
    }
}

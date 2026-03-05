using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Orpheus.Desktop.Views;

public partial class QueuePanel : UserControl
{
    // ── Queue reorder state ──────────────────────────────────
    private int _dragStartIndex = -1;
    private int _dragCurrentIndex = -1;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6;
    private QueueItem? _draggedItem;
    private static readonly QueueItem PlaceholderItem = new("", "", "");

    // Custom double-click with a looser timer
    private const int DoubleClickMs = 650;
    private DateTime _lastClickTime;
    private int _lastClickIndex = -1;

    // ── Cross-panel drop state ───────────────────────────────
    private bool _isOverForDrop;

    public QueuePanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (QueueList is not null)
        {
            QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel);
            QueueList.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Tunnel);
            QueueList.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Tunnel);
        }

        // Subscribe to managed drag service for cross-panel drops
        ManagedDragService.Instance.DragStarted += OnManagedDragStarted;
        ManagedDragService.Instance.DragMoved += OnManagedDragMoved;
        ManagedDragService.Instance.DragEnded += OnManagedDragEnded;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        ManagedDragService.Instance.DragStarted -= OnManagedDragStarted;
        ManagedDragService.Instance.DragMoved -= OnManagedDragMoved;
        ManagedDragService.Instance.DragEnded -= OnManagedDragEnded;
    }

    // ── Managed drag service handlers (cross-panel drop) ────

    private bool IsManagedDragValid()
    {
        var svc = ManagedDragService.Instance;
        return svc.Payload is not null &&
               (svc.Payload.ContainsKey(DragFormats.TrackIndex) ||
                svc.Payload.ContainsKey(DragFormats.LibraryNodePath));
    }

    private void OnManagedDragStarted()
    {
        // Nothing special needed on start — we'll handle it in DragMoved
    }

    private void OnManagedDragMoved()
    {
        if (QueueList is null || ViewModel is null || !IsManagedDragValid()) return;

        var svc = ManagedDragService.Instance;
        if (svc.TopLevel is null) return;

        // Convert TopLevel client position → QueueList local position
        var localPos = svc.TopLevel.TranslatePoint(svc.ClientPosition, QueueList);
        if (localPos is null)
        {
            if (_isOverForDrop) LeaveDropZone();
            return;
        }

        var p = localPos.Value;
        var over = p.X >= 0 && p.Y >= 0
                && p.X <= QueueList.Bounds.Width
                && p.Y <= QueueList.Bounds.Height;

        if (over)
        {
            _isOverForDrop = true;
            var insertAt = GetDropInsertionIndex(p);
            ViewModel.InsertDropPlaceholder(insertAt);
        }
        else if (_isOverForDrop)
        {
            LeaveDropZone();
        }
    }

    private void OnManagedDragEnded()
    {
        if (_isOverForDrop && ViewModel is not null)
        {
            var svc = ManagedDragService.Instance;
            if (svc.Payload is not null)
            {
                // Remove the placeholder and use its position as the insertion index
                var insertAt = ViewModel.RemoveDropPlaceholder();
                _ = PerformDropAsync(svc.Payload, insertAt);
            }
            else
            {
                ViewModel.RemoveDropPlaceholder();
            }
        }
        else
        {
            // Dragged out without dropping — just remove placeholder
            ViewModel?.RemoveDropPlaceholder();
        }

        _isOverForDrop = false;
    }

    private void LeaveDropZone()
    {
        _isOverForDrop = false;
        ViewModel?.RemoveDropPlaceholder();
    }

    private async System.Threading.Tasks.Task PerformDropAsync(
        System.Collections.Generic.Dictionary<string, string> payload, int insertAt)
    {
        if (ViewModel is null) return;

        // Drop from TrackListPanel
        if (payload.TryGetValue(DragFormats.TrackIndex, out var trackIndexStr)
            && int.TryParse(trackIndexStr, out var trackIndex))
        {
            await ViewModel.AddSelectedTrackToQueueAsync(trackIndex, insertAt);
            return;
        }

        // Drop from LibraryPanel
        if (payload.TryGetValue(DragFormats.LibraryNodePath, out var path))
        {
            var nodeType = LibraryNodeType.Folder;
            if (payload.TryGetValue(DragFormats.LibraryNodeType, out var nodeTypeStr)
                && int.TryParse(nodeTypeStr, out var nt))
            {
                nodeType = (LibraryNodeType)nt;
            }

            switch (nodeType)
            {
                case LibraryNodeType.Playlist:
                    await ViewModel.AddPlaylistFileToQueueAsync(path, insertAt);
                    break;
                case LibraryNodeType.File:
                    ViewModel.AddFileToQueue(path, insertAt);
                    break;
                default:
                    await ViewModel.AddFolderToQueueAsync(path, insertAt);
                    break;
            }
        }
    }

    // ── Drop insertion index ─────────────────────────────────

    /// <summary>
    /// Computes where the cross-panel drop placeholder should sit.
    /// 
    /// Strategy: remove the placeholder first so the item positions are stable,
    /// compute the insertion index against the clean list using the still-valid
    /// container positions (layout hasn't re-run yet since we're synchronous on
    /// the UI thread), then return that index for re-insertion.
    /// </summary>
    private int GetDropInsertionIndex(Point queueListPoint)
    {
        if (QueueList is null || ViewModel is null)
            return -1;

        // Remove placeholder so we compute against a clean list.
        // Container positions are still valid (layout is deferred).
        var hadPlaceholder = ViewModel.DropPlaceholderIndex >= 0;
        if (hadPlaceholder)
            ViewModel.RemoveDropPlaceholder();

        var count = ViewModel.Queue.Count;
        if (count == 0) return 0;

        for (var i = 0; i < count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var itemTop = container.TranslatePoint(new Point(0, 0), QueueList);
            if (itemTop is null) continue;

            var midY = itemTop.Value.Y + container.Bounds.Height / 2;
            if (queueListPoint.Y < midY)
                return i;
        }

        return count; // after last item
    }

    // ── Auto-scroll to current track ─────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentQueueIndex))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (sender is not MainWindowViewModel viewModel)
                return;

            if (QueueList is null)
                return;

            if (viewModel.CurrentQueueIndex < 0)
                return;

            QueueList.SelectedIndex = viewModel.CurrentQueueIndex;
            QueueList.ScrollIntoView(viewModel.CurrentQueueIndex);
        });
    }

    // ── Queue reorder via pointer capture ────────────────────

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || QueueList is null)
            return;

        var point = e.GetPosition(QueueList);
        var item = GetListBoxItemAtPoint(point);
        if (item is null) return;

        var index = QueueList.IndexFromContainer(item);
        _dragStartIndex = index;
        _dragStartPoint = point;
        _isDragging = false;

        // Custom double-click detection (skip if click is on the drag handle)
        if (!IsOnDragHandle(e.Source as Avalonia.Visual))
        {
            var now = DateTime.UtcNow;
            if (_lastClickIndex == index && (now - _lastClickTime).TotalMilliseconds <= DoubleClickMs)
            {
                _lastClickIndex = -1;
                _dragStartIndex = -1; // prevent drag from starting
                _ = ViewModel.PlayQueueItemAsync(index);
                return;
            }
            _lastClickIndex = index;
            _lastClickTime = now;
        }
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartIndex < 0 || ViewModel is null || QueueList is null)
            return;

        var point = e.GetPosition(QueueList);
        if (!_isDragging)
        {
            var delta = point - _dragStartPoint;
            if (Math.Abs(delta.Y) < DragThreshold)
                return;

            // Begin drag: capture the item, replace it with a placeholder
            _isDragging = true;
            _draggedItem = ViewModel.Queue[_dragStartIndex];
            _dragCurrentIndex = _dragStartIndex;
            ViewModel.BeginDragQueueItem(_dragStartIndex);

            // Show floating preview using DragPreviewWindow
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null)
            {
                var previewText = new TextBlock
                {
                    Text = _draggedItem.PrimaryText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 200,
                    Foreground = QueueList.Foreground ?? Brushes.White,
                };
                DragPreviewService.Show(previewText, topLevel, e.GetPosition(topLevel));
            }

            e.Pointer.Capture(QueueList);
        }

        // Update preview position
        var tl = TopLevel.GetTopLevel(this);
        if (tl is not null)
        {
            DragPreviewService.Move(tl, e.GetPosition(tl));
        }

        // Determine target drop position
        var targetIndex = GetReorderInsertionIndex(point);
        if (targetIndex >= 0 && targetIndex != _dragCurrentIndex)
        {
            ViewModel.MoveDragPlaceholder(_dragCurrentIndex, targetIndex);
            _dragCurrentIndex = targetIndex;
        }
    }

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && ViewModel is not null && _draggedItem is not null)
        {
            // Finalize: replace placeholder with the real item
            ViewModel.EndDragQueueItem(_dragCurrentIndex, _draggedItem);
            DragPreviewService.Hide();
            e.Pointer.Capture(null);
        }

        _dragStartIndex = -1;
        _dragCurrentIndex = -1;
        _isDragging = false;
        _draggedItem = null;
    }

    /// <summary>
    /// Determines the insertion index based on pointer Y position by
    /// checking the midpoint of each visible ListBoxItem.
    /// </summary>
    private int GetReorderInsertionIndex(Point listBoxPoint)
    {
        if (QueueList is null || ViewModel is null)
            return -1;

        var count = ViewModel.Queue.Count;
        for (var i = 0; i < count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var itemTop = container.TranslatePoint(new Point(0, 0), QueueList);
            if (itemTop is null) continue;

            var midY = itemTop.Value.Y + container.Bounds.Height / 2;
            if (listBoxPoint.Y < midY)
                return i;
        }

        return count - 1;
    }

    // ── Helpers ──────────────────────────────────────────────

    private ListBoxItem? GetListBoxItemAtPoint(Point point)
    {
        if (QueueList is null) return null;

        var hit = QueueList.InputHitTest(point);
        if (hit is not Visual visual) return null;

        var current = visual;
        while (current is not null)
        {
            if (current is ListBoxItem lbi)
                return lbi;
            current = current.GetVisualParent() as Visual;
        }
        return null;
    }

    /// <summary>
    /// Checks whether the given visual is (or is inside) the drag handle element
    /// (the TextBlock with Tag="DragHandle").
    /// </summary>
    private static bool IsOnDragHandle(Avalonia.Visual? visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is TextBlock tb && tb.Tag is string tag && tag == "DragHandle")
                return true;
            current = current.GetVisualParent() as Avalonia.Visual;
        }
        return false;
    }

    // ── Item context menu ────────────────────────────────────

    public void OnRemoveQueueItemClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || QueueList is null) return;

        if (sender is not MenuItem menuItem) return;
        var contextMenu = menuItem.Parent as ContextMenu;
        var placementTarget = contextMenu?.PlacementTarget as Visual;
        if (placementTarget is null) return;

        var listBoxItem = FindAncestor<ListBoxItem>(placementTarget);
        if (listBoxItem is null) return;

        var index = QueueList.IndexFromContainer(listBoxItem);
        if (index >= 0)
            ViewModel.RemoveFromQueue(index);
    }

    public async void OnSaveQueueClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            await ViewModel.SaveQueueAsPlaylistAsync();
    }

    public async void OnClearQueueClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            await ViewModel.ClearQueueAsync();
    }

    private static T? FindAncestor<T>(Visual? visual) where T : Visual
    {
        var current = visual;
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current.GetVisualParent() as Visual;
        }
        return null;
    }

    // ── Options menu ─────────────────────────────────────────

    public void OnQueueOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.Open(button);
        }
    }

    public void OnSetDisplayMode(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not MenuItem { Tag: string tag }) return;
        if (Enum.TryParse<QueueDisplayMode>(tag, out var mode))
            ViewModel.QueueDisplayMode = mode;
    }

    public void OnToggleSecondaryText(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is MenuItem menuItem)
            ViewModel.ShowQueueSecondaryText = menuItem.IsChecked;
    }

    public void OnQueueOptionsMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not ContextMenu menu)
            return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem menuItem || menuItem.Tag is not string tag)
                continue;

            menuItem.IsChecked = tag switch
            {
                "TitleAlbum"             => ViewModel.QueueDisplayMode == QueueDisplayMode.TitleAlbum,
                "FileNameFolder"         => ViewModel.QueueDisplayMode == QueueDisplayMode.FileNameFolder,
                "TitleAlbumWithFallback" => ViewModel.QueueDisplayMode == QueueDisplayMode.TitleAlbumWithFallback,
                "SecondaryText"          => ViewModel.ShowQueueSecondaryText,
                _ => menuItem.IsChecked
            };
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Orpheus.Desktop.Views;

public partial class QueuePanel : UserControl
{
    private int _dragStartIndex = -1;
    private int _dragCurrentIndex = -1;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6;

    // Floating adorner and placeholder for drag visual feedback
    private Border? _dragAdorner;
    private QueueItem? _draggedItem;
    private static readonly QueueItem PlaceholderItem = new("", "", "");

    // Custom double-click with a looser timer
    private const int DoubleClickMs = 650;
    private DateTime _lastClickTime;
    private int _lastClickIndex = -1;

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
    }

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

    // ── Drag-and-drop reorder ────────────────────────────────

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
            ShowDragAdorner(_draggedItem, e);
            e.Pointer.Capture(QueueList);
        }

        // Update adorner position
        UpdateDragAdorner(e);

        // Determine target drop position
        var targetIndex = GetInsertionIndex(point);
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
            RemoveDragAdorner();
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
    private int GetInsertionIndex(Point listBoxPoint)
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

    // ── Drag adorner (floating element) ──────────────────────

    private void ShowDragAdorner(QueueItem item, PointerEventArgs e)
    {
        var adornerLayer = AdornerLayer.GetAdornerLayer(QueueList!);
        if (adornerLayer is null) return;

        // Build a visual matching the queue item template
        var primary = new TextBlock
        {
            Text = item.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = QueueList!.Foreground ?? Brushes.White,
        };

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(primary);

        if (!string.IsNullOrEmpty(item.SecondaryText))
        {
            var secondary = new TextBlock
            {
                Text = item.SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 11,
                Opacity = 0.6,
                Foreground = QueueList.Foreground ?? Brushes.White,
            };
            content.Children.Add(secondary);
        }

        _dragAdorner = new Border
        {
            Child = content,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };

        // Apply theme-aware colors
        _dragAdorner[!Border.BackgroundProperty] =
            new Avalonia.Data.Binding("Background") { Source = QueueList };

        if (Application.Current!.Resources.TryGetResource(
                "PanelFill", Application.Current.ActualThemeVariant, out var panelFill)
            && panelFill is Color fillColor)
        {
            _dragAdorner.Background = new SolidColorBrush(fillColor);
        }
        if (Application.Current.Resources.TryGetResource(
                "AccentColor", Application.Current.ActualThemeVariant, out var accent)
            && accent is Color accentColor)
        {
            _dragAdorner.BorderBrush = new SolidColorBrush(accentColor);
            _dragAdorner.BorderThickness = new Thickness(1);
        }

        AdornerLayer.SetAdornedElement(_dragAdorner, QueueList!);
        adornerLayer.Children.Add(_dragAdorner);
        UpdateDragAdorner(e);
    }

    private void UpdateDragAdorner(PointerEventArgs e)
    {
        if (_dragAdorner is null || QueueList is null) return;

        var adornerLayer = AdornerLayer.GetAdornerLayer(QueueList);
        if (adornerLayer is null) return;

        // Get position relative to the adorner layer so the floating item
        // tracks the mouse correctly regardless of scroll or layout offset.
        var pos = e.GetPosition(adornerLayer);
        _dragAdorner.RenderTransform = new TranslateTransform(pos.X + 12, pos.Y - 12);
    }

    private void RemoveDragAdorner()
    {
        if (_dragAdorner is null || QueueList is null) return;

        var adornerLayer = AdornerLayer.GetAdornerLayer(QueueList);
        adornerLayer?.Children.Remove(_dragAdorner);
        _dragAdorner = null;
    }

    private ListBoxItem? GetListBoxItemAtPoint(Point point)
    {
        if (QueueList is null) return null;

        // Hit-test on the visual tree
        var hit = QueueList.InputHitTest(point);
        if (hit is not Visual visual) return null;

        // Walk up the tree to find the ListBoxItem
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

    // ── Options menu ─────────────────────────────────────────

    public void OnQueueOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.Open(button);
        }
    }

    public void OnShowTitle(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.QueueDisplayMode = QueueDisplayMode.Title;
    }

    public void OnShowFileName(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.QueueDisplayMode = QueueDisplayMode.FileName;
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
                "Title" => ViewModel.QueueDisplayMode == QueueDisplayMode.Title,
                "FileName" => ViewModel.QueueDisplayMode == QueueDisplayMode.FileName,
                "SecondaryText" => ViewModel.ShowQueueSecondaryText,
                _ => menuItem.IsChecked
            };
        }
    }
}

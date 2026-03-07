using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Orpheus.Android;

public partial class MainView : UserControl
{
    private MobileSettingsViewModel? _settingsVm;
    private MobileViewModel? _subscribedVm;

    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Wire the settings overlay close button
        if (this.FindControl<SettingsView>("SettingsOverlay") is { } overlay)
        {
            overlay.CloseRequested += (_, _) =>
            {
                if (Vm is { } vm) vm.IsSettingsOpen = false;
                overlay.IsVisible = false;
            };
        }

        // All button clicks bubble to here
        AddHandler(Button.ClickEvent, OnAnyButtonClick, handledEventsToo: false);

        // Track drag on seek slider
        WireSeekSlider("PositionSlider");

        // Queue long-press drag reorder
        WireQueueDrag();

        // Rescan confirmation overlay
        WireRescanOverlay();

        // Long-press on library folder nodes
        WireFolderLongPress();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unsubscribe from the old VM
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        // Dispose previous settings VM before replacing
        _settingsVm?.Dispose();

        if (Vm is { } vm)
        {
            // Create the settings VM and assign it to the overlay
            _settingsVm = new MobileSettingsViewModel(vm);
            if (this.FindControl<SettingsView>("SettingsOverlay") is { } overlay)
                overlay.DataContext = _settingsVm;

            // Subscribe to IsSettingsOpen changes to drive overlay visibility from code
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;

            // Sync initial state (should always be false, but be explicit)
            SyncSettingsOverlay(vm.IsSettingsOpen);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MobileViewModel.IsSettingsOpen) && Vm is { } vm)
            SyncSettingsOverlay(vm.IsSettingsOpen);
    }

    private void SyncSettingsOverlay(bool open)
    {
        if (this.FindControl<SettingsView>("SettingsOverlay") is { } overlay)
            overlay.IsVisible = open;
    }

    // ── Universal click dispatcher ────────────────────────────────────

    private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button btn) return;
        var vm = Vm;
        if (vm is null) return;

        switch (btn.Name)
        {
            // ── Tabs ─────────────────────────────────────────────
            case "LibraryTabButton": vm.ActiveTab = MobileTab.Library; break;
            case "QueueTabButton":   vm.ActiveTab = MobileTab.Queue;   break;

            // ── Settings ──────────────────────────────────────────
            case "SettingsButton":
                vm.IsSettingsOpen = true;   // overlay visibility driven by OnVmPropertyChanged
                break;

            // ── Library folder management (quick-add in header) ───
            case "AddFolderButton":
                _ = PickAndAddFolderAsync(this, vm);
                break;

            // ── Library navigation ────────────────────────────────
            case "LibraryBackButton":
                vm.NavigateBack();
                break;

            case "FolderDrillButton":
                if (btn.DataContext is LibraryNode drillNode && drillNode.IsFolder)
                    vm.NavigateInto(drillNode);
                break;

            case "PlaylistLoadButton":
                if (btn.DataContext is LibraryNode loadNode && loadNode.IsPlaylist)
                    _ = vm.LoadPlaylistAsync(loadNode);
                break;

            case "FolderPlayButton":
                if (btn.DataContext is LibraryNode playNode)
                {
                    if (playNode.IsPlaylist) _ = vm.LoadPlaylistAsync(playNode);
                    else                     _ = vm.PlayFolderAsync(playNode);
                }
                break;

            case "FolderEnqueueButton":
                if (btn.DataContext is LibraryNode enqueueNode)
                {
                    if (enqueueNode.IsPlaylist) _ = vm.EnqueuePlaylistAsync(enqueueNode);
                    else                        _ = vm.EnqueueFolder(enqueueNode);
                }
                break;

            case "SaveQueueButton":
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is not null) _ = vm.SaveQueueAsPlaylistAsync(topLevel);
                break;
            }

            // ── Track list ────────────────────────────────────────
            case "TrackPlayButton":
                if (btn.DataContext is TrackRow playRow)
                    _ = vm.PlayTrackAsync(playRow);
                break;

            case "TrackEnqueueButton":
                if (btn.DataContext is TrackRow enqueueRow)
                    vm.EnqueueTrack(enqueueRow);
                break;

            // ── Queue ─────────────────────────────────────────────
            case "ClearQueueButton":
                _ = vm.ClearQueueAsync();
                break;

            case "QueueItemPlayButton":
                if (btn.DataContext is QueueItem playItem)
                {
                    var idx = vm.Queue.IndexOf(playItem);
                    if (idx >= 0) _ = vm.PlayQueueIndexAsync(idx);
                }
                break;

            case "QueueItemRemoveButton":
                if (btn.DataContext is QueueItem removeItem)
                {
                    var idx = vm.Queue.IndexOf(removeItem);
                    if (idx >= 0) _ = vm.RemoveFromQueueAsync(idx);
                }
                break;

            // ── Transport ─────────────────────────────────────────
            case "PlayPauseButton": _ = vm.TogglePlayPauseAsync(); break;
            case "PreviousButton":  _ = vm.PlayPreviousAsync();    break;
            case "NextButton":      _ = vm.PlayNextAsync();        break;
            case "ShuffleButton":   _ = vm.ToggleShuffleAsync();   break;
            case "RepeatButton":    _ = vm.ToggleRepeatAsync();    break;

            // ── Rescan confirmation overlay ───────────────────────
            case "RescanConfirmYesButton":
                if (_rescanPendingPath is { } rescanPath)
                    _ = vm.RescanFolderAsync(rescanPath);
                HideRescanConfirm();
                break;

            case "RescanConfirmNoButton":
                HideRescanConfirm();
                break;
        }
    }

    // ── Folder picker ─────────────────────────────────────────────────

    private static async System.Threading.Tasks.Task PickAndAddFolderAsync(
        Control anchor, MobileViewModel vm)
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

    // ── Queue long-press drag reorder ────────────────────────────────

    // How long the finger must be held before drag begins (ms).
    private const int LongPressMs = 400;
    // How many pixels the finger can drift before the long-press is cancelled.
    private const double LongPressCancelDistance = 8.0;
    // Minimum pixels of movement required to reorder (avoids jitter).
    private const double ReorderThreshold = 4.0;

    // ── Rescan confirmation overlay ───────────────────────────────────

    private Grid?      _rescanOverlay;
    private TextBlock? _rescanLabel;
    private string?    _rescanPendingPath;

    private void WireRescanOverlay()
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            _rescanOverlay = FindDescendantByName<Grid>(this, "RescanConfirmOverlay");
            _rescanLabel   = FindDescendantByName<TextBlock>(this, "RescanConfirmLabel");
            if (_rescanOverlay is null) return;
            LayoutUpdated -= handler;
        };
        LayoutUpdated += handler;
    }

    private void ShowRescanConfirm(LibraryNode node)
    {
        if (_rescanOverlay is null || _rescanLabel is null) return;
        _rescanPendingPath = node.Path;
        _rescanLabel.Text = $"Rescan \"{node.Name}\"?";
        _rescanOverlay.IsVisible = true;
    }

    private void HideRescanConfirm()
    {
        _rescanPendingPath = null;
        if (_rescanOverlay is not null)
            _rescanOverlay.IsVisible = false;
    }

    // ── Queue long-press drag reorder ────────────────────────────────

    private ListBox?   _queueList;
    private Canvas?    _dragPreviewCanvas;
    private Border?    _dragPreviewBorder;
    private TextBlock? _dragPreviewText;

    private CancellationTokenSource? _longPressCts;
    private bool   _isDragging;
    private int    _dragFromIndex;
    private int    _dragCurrentIndex;
    private Point  _dragPressPoint;

    // ── Folder long-press (rescan) ────────────────────────────────────

    private CancellationTokenSource? _folderLongPressCts;
    private Point _folderPressPoint;
    private LibraryNode? _folderLongPressNode;

    private void WireFolderLongPress()
    {
        AddHandler(PointerPressedEvent,  OnFolderPointerPressed,  RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent,    OnFolderPointerMoved,    RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnFolderPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnFolderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only track presses that originate on a FolderDrillButton
        if (!IsFolderDrillSource(e.Source, out var node)) return;

        _folderLongPressCts?.Cancel();
        _folderLongPressCts = new CancellationTokenSource();
        _folderPressPoint   = e.GetPosition(this);
        _folderLongPressNode = node;

        var token = _folderLongPressCts.Token;
        _ = Task.Delay(LongPressMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || _folderLongPressNode is null) return;
                ShowRescanConfirm(_folderLongPressNode);
            });
        });
    }

    private void OnFolderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_folderLongPressCts is null || _folderLongPressNode is null) return;
        var pos   = e.GetPosition(this);
        var delta = pos - _folderPressPoint;
        if (Math.Abs(delta.X) > LongPressCancelDistance || Math.Abs(delta.Y) > LongPressCancelDistance)
        {
            _folderLongPressCts.Cancel();
            _folderLongPressNode = null;
        }
    }

    private void OnFolderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _folderLongPressCts?.Cancel();
        _folderLongPressNode = null;
    }

    private static bool IsFolderDrillSource(object? source, out LibraryNode? node)
    {
        node = null;
        // Walk up the visual tree from the event source to find a FolderDrillButton
        var element = source as Avalonia.Visual;
        while (element is not null)
        {
            if (element is Button btn && btn.Name == "FolderDrillButton"
                && btn.DataContext is LibraryNode n && n.IsFolder)
            {
                node = n;
                return true;
            }
            element = element.GetVisualParent() as Avalonia.Visual;
        }
        return false;
    }

    // ── Queue long-press drag reorder ────────────────────────────────

    private void WireQueueDrag()
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            _queueList         = FindDescendantByName<ListBox>(this, "QueueList");
            _dragPreviewCanvas = FindDescendantByName<Canvas>(this, "QueueDragPreviewCanvas");
            _dragPreviewBorder = FindDescendantByName<Border>(this, "QueueDragPreviewBorder");
            _dragPreviewText   = FindDescendantByName<TextBlock>(this, "QueueDragPreviewText");

            if (_queueList is null) return;

            _queueList.AddHandler(PointerPressedEvent,  OnQueuePointerPressed,  RoutingStrategies.Tunnel);
            _queueList.AddHandler(PointerMovedEvent,    OnQueuePointerMoved,    RoutingStrategies.Tunnel);
            _queueList.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Tunnel);
            _queueList.AddHandler(PointerCaptureLostEvent, OnQueuePointerCaptureLost, RoutingStrategies.Tunnel);

            LayoutUpdated -= handler;
        };
        LayoutUpdated += handler;
    }

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_queueList is null || Vm is null) return;

        _longPressCts?.Cancel();
        _longPressCts = new CancellationTokenSource();
        _dragPressPoint = e.GetPosition(_queueList);
        _isDragging = false;

        var token = _longPressCts.Token;
        var pressPoint = _dragPressPoint;

        _ = System.Threading.Tasks.Task.Delay(LongPressMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || _queueList is null || Vm is null) return;

                var idx = GetQueueIndexAtPoint(pressPoint);
                if (idx < 0) return;

                _isDragging = true;
                _dragFromIndex = idx;
                _dragCurrentIndex = idx;

                // Show drag preview
                if (_dragPreviewCanvas is not null && _dragPreviewBorder is not null && _dragPreviewText is not null)
                {
                    var item = Vm.Queue[idx];
                    _dragPreviewText.Text = item.Primary;
                    Canvas.SetLeft(_dragPreviewBorder, pressPoint.X - 20);
                    Canvas.SetTop(_dragPreviewBorder,  pressPoint.Y - 28);
                    _dragPreviewCanvas.IsVisible = true;
                }

                // Capture pointer so PointerMoved/Released always arrive here
                e.Pointer.Capture(_queueList);
            });
        });
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueList is null || Vm is null) return;

        var pos = e.GetPosition(_queueList);

        // Cancel long-press if finger drifted too far before timer fired
        if (!_isDragging)
        {
            var delta = pos - _dragPressPoint;
            if (Math.Abs(delta.X) > LongPressCancelDistance ||
                Math.Abs(delta.Y) > LongPressCancelDistance)
            {
                _longPressCts?.Cancel();
            }
            return;
        }

        e.Handled = true;

        // Move preview border to follow finger
        if (_dragPreviewCanvas is not null && _dragPreviewBorder is not null)
        {
            Canvas.SetLeft(_dragPreviewBorder, pos.X - 20);
            Canvas.SetTop(_dragPreviewBorder,  pos.Y - 28);
        }

        // Determine which index the finger is hovering over
        var targetIdx = GetQueueIndexAtPoint(pos);
        if (targetIdx < 0 || targetIdx == _dragCurrentIndex) return;

        var delta2 = Math.Abs(pos.Y - _dragPressPoint.Y);
        if (delta2 < ReorderThreshold) return;

        Vm.MoveQueueItem(_dragCurrentIndex, targetIdx);
        _dragCurrentIndex = targetIdx;
    }

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndQueueDrag(e.Pointer);
    }

    private void OnQueuePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndQueueDrag(null);
    }

    private void EndQueueDrag(IPointer? pointer)
    {
        _longPressCts?.Cancel();
        _isDragging = false;

        if (_dragPreviewCanvas is not null)
            _dragPreviewCanvas.IsVisible = false;

        pointer?.Capture(null);
    }

    /// <summary>
    /// Returns the queue list index of the item whose row midpoint is closest to
    /// <paramref name="point"/> (same approach as the desktop drag panel).
    /// Returns -1 if there are no items.
    /// </summary>
    private int GetQueueIndexAtPoint(Point point)
    {
        if (_queueList is null || Vm is null) return -1;

        var count = Vm.Queue.Count;
        for (var i = 0; i < count; i++)
        {
            if (_queueList.ContainerFromIndex(i) is not ListBoxItem container) continue;
            var itemTop = container.TranslatePoint(new Point(0, 0), _queueList);
            if (itemTop is null) continue;
            var midY = itemTop.Value.Y + container.Bounds.Height / 2;
            if (point.Y < midY) return i;
        }

        return count - 1;
    }

    // ── Seek slider drag tracking ─────────────────────────────────────

    private void WireSeekSlider(string name)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (FindDescendantByName<Slider>(this, name) is { } slider)
            {
                slider.AddHandler(PointerPressedEvent, (_, _) =>
                {
                    if (Vm is { } v) v.IsUserSeekingPosition = true;
                }, handledEventsToo: true);

                slider.AddHandler(PointerReleasedEvent, (_, _) =>
                {
                    if (Vm is { } v)
                    {
                        // Seek to wherever the thumb ended up (covers both tap and drag).
                        _ = v.SeekToPositionAsync(slider.Value);
                        v.IsUserSeekingPosition = false;
                    }
                }, handledEventsToo: true);

                LayoutUpdated -= handler;
            }
        };
        LayoutUpdated += handler;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private MobileViewModel? Vm => DataContext as MobileViewModel;

    private static T? FindDescendantByName<T>(ILogical root, string name) where T : Control
    {
        foreach (var child in root.LogicalChildren)
        {
            if (child is T ctrl && ctrl.Name == name) return ctrl;
            if (child is ILogical sub)
            {
                var found = FindDescendantByName<T>(sub, name);
                if (found is not null) return found;
            }
        }
        return null;
    }
}

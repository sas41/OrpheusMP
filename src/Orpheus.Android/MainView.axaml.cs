using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

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

            case "RemoveFolderButton":
                if (btn.DataContext is string folderPath)
                    _ = vm.RemoveFolderAsync(folderPath);
                break;

            // ── Library navigation ────────────────────────────────
            case "LibraryBackButton":
                vm.NavigateBack();
                break;

            case "FolderDrillButton":
                if (btn.DataContext is LibraryNode drillNode)
                    vm.NavigateInto(drillNode);
                break;

            case "FolderPlayButton":
                if (btn.DataContext is LibraryNode playFolderNode)
                    _ = vm.PlayFolderAsync(playFolderNode);
                break;

            case "FolderEnqueueButton":
                if (btn.DataContext is LibraryNode enqueueFolderNode)
                    vm.EnqueueFolder(enqueueFolderNode);
                break;

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

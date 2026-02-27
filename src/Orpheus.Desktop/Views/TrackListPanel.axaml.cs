using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Orpheus.Desktop.Views;

public partial class TrackListPanel : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragPending;
    private bool _isManagedDragging;
    private IPointer? _capturedPointer;
    private TopLevel? _dragTopLevel;
    private const double DragThreshold = 8;

    public TrackListPanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyColumnVisibility();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (TracksGrid is not null)
        {
            TracksGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
            TracksGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);
        }
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(TracksGrid).Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(TracksGrid);
            _isDragPending = true;
        }
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragPending || ViewModel is null || TracksGrid is null)
            return;

        if (!e.GetCurrentPoint(TracksGrid).Properties.IsLeftButtonPressed)
        {
            _isDragPending = false;
            return;
        }

        var pos = e.GetPosition(TracksGrid);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _isDragPending = false;

        var index = ViewModel.SelectedTrackIndex;
        if (index < 0) return;

        var trackRow = index < ViewModel.Tracks.Count ? ViewModel.Tracks[index] : null;
        var label = trackRow?.Title ?? trackRow?.FileName ?? "Track";

        var payload = new System.Collections.Generic.Dictionary<string, string>
        {
            [DragFormats.TrackIndex] = index.ToString(),
            [DragFormats.DragLabel] = label,
        };

        // Begin managed drag: capture pointer, show preview, notify drop targets
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        _dragTopLevel = topLevel;
        _isManagedDragging = true;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(TracksGrid);

        // Attach top-level handlers to receive events even when pointer leaves this panel
        topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        var previewText = new TextBlock
        {
            Text = label,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            Foreground = TracksGrid.Foreground ?? Avalonia.Media.Brushes.White,
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
        _isDragPending = false;

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

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public async void OnTrackDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.PlaySelectedAsync();
    }

    public void OnTrackColumnsMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not ContextMenu menu)
            return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem menuItem || menuItem.Tag is not string tag)
                continue;

            menuItem.IsChecked = tag switch
            {
                "Title" => ViewModel.ShowTitle,
                "Artist" => ViewModel.ShowArtist,
                "Album" => ViewModel.ShowAlbum,
                "Filename" => ViewModel.ShowFileName,
                "TrackNumber" => ViewModel.ShowTrackNumber,
                "DiscNumber" => ViewModel.ShowDiscNumber,
                "Year" => ViewModel.ShowYear,
                "Genre" => ViewModel.ShowGenre,
                "Bitrate" => ViewModel.ShowBitrate,
                "Time" => ViewModel.ShowLength,
                "Format" => ViewModel.ShowFormat,
                _ => menuItem.IsChecked
            };
        }
    }

    public void OnTrackSortMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not ContextMenu menu)
            return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem menuItem || menuItem.Tag is not string tag)
                continue;

            if (tag == "Ascending")
            {
                menuItem.IsChecked = ViewModel.SortAscending;
                continue;
            }

            menuItem.IsChecked = tag switch
            {
                "Title" => ViewModel.SortField == TrackSortField.Title,
                "Artist" => ViewModel.SortField == TrackSortField.Artist,
                "Album" => ViewModel.SortField == TrackSortField.Album,
                "FileName" => ViewModel.SortField == TrackSortField.FileName,
                "TrackNumber" => ViewModel.SortField == TrackSortField.TrackNumber,
                "Year" => ViewModel.SortField == TrackSortField.Year,
                "Duration" => ViewModel.SortField == TrackSortField.Duration,
                "DateAdded" => ViewModel.SortField == TrackSortField.DateAdded,
                "Bitrate" => ViewModel.SortField == TrackSortField.Bitrate,
                _ => menuItem.IsChecked
            };
        }
    }

    public void OnTrackFilterMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not ContextMenu menu)
            return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem menuItem || menuItem.Tag is not string tag)
                continue;

            menuItem.IsChecked = tag switch
            {
                "HideMissingTitle" => ViewModel.HideMissingTitle,
                "HideMissingArtist" => ViewModel.HideMissingArtist,
                "HideMissingAlbum" => ViewModel.HideMissingAlbum,
                "HideMissingGenre" => ViewModel.HideMissingGenre,
                "HideMissingTrackNumber" => ViewModel.HideMissingTrackNumber,
                _ => menuItem.IsChecked
            };
        }
    }

    public void OnToggleColumnClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not MenuItem menuItem || menuItem.Tag is not string tag)
            return;

        var isChecked = menuItem.IsChecked;
        switch (tag)
        {
            case "Title":
                ViewModel.ShowTitle = isChecked;
                break;
            case "Artist":
                ViewModel.ShowArtist = isChecked;
                break;
            case "Album":
                ViewModel.ShowAlbum = isChecked;
                break;
            case "Filename":
                ViewModel.ShowFileName = isChecked;
                break;
            case "TrackNumber":
                ViewModel.ShowTrackNumber = isChecked;
                break;
            case "DiscNumber":
                ViewModel.ShowDiscNumber = isChecked;
                break;
            case "Year":
                ViewModel.ShowYear = isChecked;
                break;
            case "Genre":
                ViewModel.ShowGenre = isChecked;
                break;
            case "Bitrate":
                ViewModel.ShowBitrate = isChecked;
                break;
            case "Time":
                ViewModel.ShowLength = isChecked;
                break;
            case "Format":
                ViewModel.ShowFormat = isChecked;
                break;
        }

        ApplyColumnVisibility();
    }

    public void OnSortMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not MenuItem menuItem || menuItem.Tag is not string tag)
            return;

        var field = tag switch
        {
            "Title" => TrackSortField.Title,
            "Artist" => TrackSortField.Artist,
            "Album" => TrackSortField.Album,
            "FileName" => TrackSortField.FileName,
            "TrackNumber" => TrackSortField.TrackNumber,
            "Year" => TrackSortField.Year,
            "Duration" => TrackSortField.Duration,
            "DateAdded" => TrackSortField.DateAdded,
            "Bitrate" => TrackSortField.Bitrate,
            _ => TrackSortField.Title
        };

        ViewModel.SetSortField(field);
    }

    public void OnSortDirectionClicked(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleSortDirection();
    }

    public void OnFilterMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (sender is not MenuItem menuItem || menuItem.Tag is not string tag)
            return;

        var isChecked = menuItem.IsChecked;
        switch (tag)
        {
            case "HideMissingTitle":
                ViewModel.HideMissingTitle = isChecked;
                break;
            case "HideMissingArtist":
                ViewModel.HideMissingArtist = isChecked;
                break;
            case "HideMissingAlbum":
                ViewModel.HideMissingAlbum = isChecked;
                break;
            case "HideMissingGenre":
                ViewModel.HideMissingGenre = isChecked;
                break;
            case "HideMissingTrackNumber":
                ViewModel.HideMissingTrackNumber = isChecked;
                break;
        }
    }

    public void OnSortButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        button.ContextMenu?.Open(button);
    }

    public void OnFilterButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        button.ContextMenu?.Open(button);
    }

    public void OnTracksGridLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyColumnVisibility();
    }

    private void ApplyColumnVisibility()
    {
        if (ViewModel is null)
            return;

        if (TracksGrid is null)
            return;

        if (TracksGrid.Columns.Count >= 11)
        {
            TracksGrid.Columns[0].IsVisible = ViewModel.ShowTitle;
            TracksGrid.Columns[1].IsVisible = ViewModel.ShowArtist;
            TracksGrid.Columns[2].IsVisible = ViewModel.ShowAlbum;
            TracksGrid.Columns[3].IsVisible = ViewModel.ShowFileName;
            TracksGrid.Columns[4].IsVisible = ViewModel.ShowTrackNumber;
            TracksGrid.Columns[5].IsVisible = ViewModel.ShowDiscNumber;
            TracksGrid.Columns[6].IsVisible = ViewModel.ShowYear;
            TracksGrid.Columns[7].IsVisible = ViewModel.ShowGenre;
            TracksGrid.Columns[8].IsVisible = ViewModel.ShowBitrate;
            TracksGrid.Columns[9].IsVisible = ViewModel.ShowLength;
            TracksGrid.Columns[10].IsVisible = ViewModel.ShowFormat;
        }

        Dispatcher.UIThread.Post(() => TracksGrid.InvalidateMeasure());
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lang = Orpheus.Desktop.Lang;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Orpheus.Desktop.Views;

public partial class TrackListPanel : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragPending;
    private bool _isManagedDragging;
    private bool _isInternalReordering;
    private int _currentInsertIndex = -1;
    private TrackRow? _pressedRow;
    private IReadOnlyList<int> _pressedSelection = Array.Empty<int>();
    private IReadOnlyList<int> _activeDragSelection = Array.Empty<int>();
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
            TracksGrid.SelectionChanged += OnTracksGridSelectionChanged;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (TracksGrid is not null)
            TracksGrid.SelectionChanged -= OnTracksGridSelectionChanged;
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TracksGrid is null)
            return;

        if (!e.GetCurrentPoint(TracksGrid).Properties.IsLeftButtonPressed)
            return;

        var source = e.Source as Visual;
        if (source is null)
            return;

        var visual = source;
        while (visual is not null)
        {
            if (visual is DataGridRow)
                break;
            if (visual == TracksGrid)
            {
                visual = null;
                break;
            }
            visual = visual.GetVisualParent();
        }

        if (visual is not DataGridRow row)
            return;

        _dragStartPoint = e.GetPosition(TracksGrid);
        _isDragPending = true;
        _pressedRow = row.DataContext as TrackRow;
        _pressedSelection = GetSelectedIndices();
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragPending || ViewModel is null || TracksGrid is null)
            return;

        if (!e.GetCurrentPoint(TracksGrid).Properties.IsLeftButtonPressed)
        {
            _isDragPending = false;
            _pressedRow = null;
            _pressedSelection = Array.Empty<int>();
            return;
        }

        var pos = e.GetPosition(TracksGrid);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _isDragPending = false;

        if (_pressedSelection.Count > 1)
        {
            RestoreSelection(_pressedSelection);
        }
        else if (_pressedRow is not null)
        {
            EnsureRowSelectedForDrag(_pressedRow);
        }

        var selectedIndices = GetSelectedIndices();
        _activeDragSelection = selectedIndices;
        var index = selectedIndices.Count > 0 ? selectedIndices[0] : ViewModel.SelectedTrackIndex;
        if (index < 0) return;

        var trackRow = index < ViewModel.Tracks.Count ? ViewModel.Tracks[index] : null;
        var label = selectedIndices.Count > 1
            ? $"{selectedIndices.Count} tracks"
            : trackRow?.Title ?? trackRow?.FileName ?? Lang.Resources.Track;

        var payload = new Dictionary<string, string>
        {
            [DragFormats.TrackIndex] = index.ToString(),
            [DragFormats.DragLabel] = label,
        };

        if (selectedIndices.Count > 1)
            payload[DragFormats.TrackIndices] = string.Join(",", selectedIndices);

        _isInternalReordering = ViewModel is not null && !ViewModel.IsSearchActive;

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

        if (_isInternalReordering && TracksGrid is not null)
        {
            var point = e.GetPosition(TracksGrid);
            if (IsPointerOverTracksGrid(point))
            {
                var insertIndex = GetTrackInsertionIndex(point);
                ShowInsertIndicator(insertIndex);
            }
            else
            {
                HideInsertIndicator();
            }
        }
        else
        {
            HideInsertIndicator();
        }
    }

    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isManagedDragging) return;

        if (_isInternalReordering && TracksGrid is not null && ViewModel is not null)
        {
            var point = e.GetPosition(TracksGrid);
            if (IsPointerOverTracksGrid(point))
            {
                var insertIndex = GetTrackInsertionIndex(point);
                ViewModel.MoveTrackRows(_activeDragSelection, insertIndex);
            }
        }

        EndManagedDrag();
    }

    private void EndManagedDrag()
    {
        _isManagedDragging = false;
        _isInternalReordering = false;
        _isDragPending = false;
        _pressedRow = null;
        _pressedSelection = Array.Empty<int>();
        _activeDragSelection = Array.Empty<int>();
        HideInsertIndicator();

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

    private void OnTracksGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncSelectedRows();
    }

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
                menuItem.IsChecked = ViewModel.IsTrackSortEnabled && ViewModel.SortAscending;
                continue;
            }

            if (!ViewModel.IsTrackSortEnabled)
            {
                menuItem.IsChecked = false;
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
        SyncSelectedRows();
    }

    public async void OnSavePlaylistOrderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            await ViewModel.SaveTrackOrderAsync();
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

    private void SyncSelectedRows()
    {
        if (TracksGrid is null || ViewModel is null)
            return;

        ViewModel.SetSelectedTrackIndices(GetSelectedIndices());
    }

    private void EnsureRowSelectedForDrag(TrackRow row)
    {
        if (TracksGrid?.SelectedItems is null)
            return;

        if (!TracksGrid.SelectedItems.OfType<TrackRow>().Any(selected => selected == row))
        {
            TracksGrid.SelectedItems.Clear();
            TracksGrid.SelectedItems.Add(row);
            SyncSelectedRows();
        }
    }

    private IReadOnlyList<int> GetSelectedIndices()
    {
        if (TracksGrid is null || ViewModel is null)
            return Array.Empty<int>();

        return TracksGrid.SelectedItems?
            .OfType<TrackRow>()
            .Select(row => ViewModel.Tracks.IndexOf(row))
            .Where(index => index >= 0)
            .Distinct()
            .OrderBy(index => index)
            .ToArray() ?? Array.Empty<int>();
    }

    private void RestoreSelection(IReadOnlyList<int> indices)
    {
        if (TracksGrid?.SelectedItems is null || ViewModel is null)
            return;

        TracksGrid.SelectedItems.Clear();
        foreach (var index in indices)
        {
            if (index >= 0 && index < ViewModel.Tracks.Count)
                TracksGrid.SelectedItems.Add(ViewModel.Tracks[index]);
        }

        SyncSelectedRows();
    }

    private int GetTrackInsertionIndex(Point point)
    {
        if (TracksGrid?.ItemsSource is null)
            return 0;

        var rows = TracksGrid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Bounds.Y).ToList();
        foreach (var row in rows)
        {
            var rowTop = row.TranslatePoint(new Point(0, 0), TracksGrid);
            if (rowTop is null)
                continue;

            var midY = rowTop.Value.Y + row.Bounds.Height / 2;
            if (point.Y < midY)
                return GetRowIndex(row);
        }

        return ViewModel?.Tracks.Count ?? 0;
    }

    private void ShowInsertIndicator(int index)
    {
        if (TracksGrid is null || TrackInsertIndicator is null)
            return;

        _currentInsertIndex = index;

        var rows = TracksGrid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Bounds.Y).ToList();
        double top;

        if (rows.Count == 0)
        {
            top = TracksGrid.ColumnHeaderHeight;
        }
        else if (index <= 0)
        {
            var firstTop = rows[0].TranslatePoint(new Point(0, 0), TracksGrid);
            top = firstTop?.Y ?? TracksGrid.ColumnHeaderHeight;
        }
        else if (index >= rows.Count)
        {
            var last = rows[^1];
            var lastTop = last.TranslatePoint(new Point(0, 0), TracksGrid);
            top = (lastTop?.Y ?? TracksGrid.ColumnHeaderHeight) + last.Bounds.Height;
        }
        else
        {
            var rowTop = rows[index].TranslatePoint(new Point(0, 0), TracksGrid);
            top = rowTop?.Y ?? TracksGrid.ColumnHeaderHeight;
        }

        TrackInsertIndicator.Margin = new Thickness(8, Math.Max(TracksGrid.ColumnHeaderHeight, top) - 1.5, 8, 0);
        TrackInsertIndicator.VerticalAlignment = VerticalAlignment.Top;
        TrackInsertIndicator.IsVisible = true;
    }

    private void HideInsertIndicator()
    {
        _currentInsertIndex = -1;
        if (TrackInsertIndicator is not null)
            TrackInsertIndicator.IsVisible = false;
    }

    private int GetRowIndex(DataGridRow row)
    {
        if (ViewModel is null || row.DataContext is not TrackRow trackRow)
            return -1;

        return ViewModel.Tracks.IndexOf(trackRow);
    }

    private bool IsPointerOverTracksGrid(Point point)
    {
        if (TracksGrid is null)
            return false;

        return point.X >= 0 && point.Y >= 0
            && point.X <= TracksGrid.Bounds.Width
            && point.Y <= TracksGrid.Bounds.Height;
    }
}

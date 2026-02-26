using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace Orpheus.Desktop.Views;

public partial class TrackListPanel : UserControl
{
    public TrackListPanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyColumnVisibility();
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using Orpheus.Core.Library;
using Orpheus.Core.Metadata;
using Orpheus.Core.Playback;
using Orpheus.Core.Media;
using Orpheus.Core.Playlist;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.IO;

namespace Orpheus.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(BerryTheme);
    }

    private static readonly ResourceDictionary BerryTheme = new()
    {
        ["AppBgTop"] = Color.Parse("#15171A"),
        ["AppBgBottom"] = Color.Parse("#1E242B"),
        ["AccentColor"] = Color.Parse("#E65B6C"),
        ["AccentSoft"] = Color.Parse("#3A1E25"),
        ["PanelFill"] = Color.Parse("#1D232B"),
        ["PanelStroke"] = Color.Parse("#2A343F"),
        ["TextPrimary"] = Color.Parse("#EDF1F4"),
        ["TextMuted"] = Color.Parse("#A5B2BC"),
        ["RowHover"] = Color.Parse("#26313B"),
        ["RowSelected"] = Color.Parse("#3A1F24"),
        ["ButtonFill"] = Color.Parse("#26303A"),
        ["ButtonHover"] = Color.Parse("#323E4A"),
        ["ButtonPressed"] = Color.Parse("#1C232B"),
        ["AccentText"] = Color.Parse("#FFFFFF"),
        ["AppSurfaceBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#15171A"), 0),
                new GradientStop(Color.Parse("#1E242B"), 1)
            }
        },
        ["SheenBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#3A1E25"), 0),
                new GradientStop(Colors.Transparent, 1)
            }
        }
    };

}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMediaLibrary _library;
    private readonly FolderScanner _scanner;
    private readonly PlayerController _controller;
    private readonly VlcPlayer _player;
    private readonly ObservableCollection<LibraryNode> _libraryRoots = new();
    private readonly ObservableCollection<TrackRow> _tracks = new();
    private readonly ObservableCollection<QueueItem> _queue = new();
    private IReadOnlyList<LibraryTrack> _allTracks = Array.Empty<LibraryTrack>();
    private IReadOnlyList<LibraryTrack> _currentTracks = Array.Empty<LibraryTrack>();
    private IReadOnlyList<LibraryTrack> _viewTracks = Array.Empty<LibraryTrack>();
    private readonly string _databasePath;
    private readonly string _musicFolder;

    private string _librarySummary = "Library loading";
    private string _queueSummary = "Queue empty";
    private string _nowPlayingTitle = "Nothing playing";
    private string _nowPlayingArtist = "";
    private string _nowPlayingAlbum = "";
    private string _nowPlayingTime = "00:00 / 00:00";
    private double _playbackDuration;
    private double _playbackPosition;
    private double _volume = 72;
    private string _searchQuery = string.Empty;
    private int _selectedTrackIndex = -1;
    private int _currentQueueIndex = -1;
    private TrackSortField _sortField = TrackSortField.Title;
    private bool _sortAscending = true;
    private bool _hideMissingTitle;
    private bool _hideMissingArtist;
    private bool _hideMissingAlbum;
    private bool _hideMissingGenre;
    private bool _hideMissingTrackNumber;
    private bool _isShuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private bool _showTitle = true;
    private bool _showArtist = true;
    private bool _showAlbum = true;
    private bool _showFileName = false;
    private bool _showLength = true;
    private bool _showFormat = true;
    private bool _showTrackNumber = false;
    private bool _showDiscNumber = false;
    private bool _showYear = false;
    private bool _showGenre = false;
    private bool _showBitrate = false;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LibrarySummary
    {
        get => _librarySummary;
        private set => SetField(ref _librarySummary, value);
    }

    public string QueueSummary
    {
        get => _queueSummary;
        private set => SetField(ref _queueSummary, value);
    }

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set => SetField(ref _nowPlayingTitle, value);
    }

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set => SetField(ref _nowPlayingArtist, value);
    }

    public string NowPlayingAlbum
    {
        get => _nowPlayingAlbum;
        private set => SetField(ref _nowPlayingAlbum, value);
    }

    public string NowPlayingTime
    {
        get => _nowPlayingTime;
        private set => SetField(ref _nowPlayingTime, value);
    }

    public double PlaybackDuration
    {
        get => _playbackDuration;
        private set => SetField(ref _playbackDuration, value);
    }

    public double PlaybackPosition
    {
        get => _playbackPosition;
        private set => SetField(ref _playbackPosition, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetField(ref _volume, value))
            {
                _player.Volume = (int)Math.Round(value);
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                _ = LoadLibraryAsync(_searchQuery);
            }
        }
    }

    public int SelectedTrackIndex
    {
        get => _selectedTrackIndex;
        set => SetField(ref _selectedTrackIndex, value);
    }

    public int CurrentQueueIndex
    {
        get => _currentQueueIndex;
        private set => SetField(ref _currentQueueIndex, value);
    }

    public TrackSortField SortField
    {
        get => _sortField;
        private set => SetField(ref _sortField, value);
    }

    public bool SortAscending
    {
        get => _sortAscending;
        private set => SetField(ref _sortAscending, value);
    }

    public bool HideMissingTitle
    {
        get => _hideMissingTitle;
        set
        {
            if (SetField(ref _hideMissingTitle, value))
                _ = RefreshTrackViewAsync();
        }
    }

    public bool HideMissingArtist
    {
        get => _hideMissingArtist;
        set
        {
            if (SetField(ref _hideMissingArtist, value))
                _ = RefreshTrackViewAsync();
        }
    }

    public bool HideMissingAlbum
    {
        get => _hideMissingAlbum;
        set
        {
            if (SetField(ref _hideMissingAlbum, value))
                _ = RefreshTrackViewAsync();
        }
    }

    public bool HideMissingGenre
    {
        get => _hideMissingGenre;
        set
        {
            if (SetField(ref _hideMissingGenre, value))
                _ = RefreshTrackViewAsync();
        }
    }

    public bool HideMissingTrackNumber
    {
        get => _hideMissingTrackNumber;
        set
        {
            if (SetField(ref _hideMissingTrackNumber, value))
                _ = RefreshTrackViewAsync();
        }
    }

    public bool IsShuffleEnabled
    {
        get => _isShuffleEnabled;
        private set => SetField(ref _isShuffleEnabled, value);
    }

    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (SetField(ref _repeatMode, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRepeatEnabled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatLabel)));
            }
        }
    }

    public bool IsRepeatEnabled => _repeatMode != RepeatMode.Off;

    public string RepeatLabel => _repeatMode switch
    {
        RepeatMode.All => "Repeat All",
        RepeatMode.One => "Repeat One",
        _ => "Repeat"
    };

    public bool ShowTitle
    {
        get => _showTitle;
        set => SetField(ref _showTitle, value);
    }

    public bool ShowArtist
    {
        get => _showArtist;
        set => SetField(ref _showArtist, value);
    }

    public bool ShowAlbum
    {
        get => _showAlbum;
        set => SetField(ref _showAlbum, value);
    }

    public bool ShowFileName
    {
        get => _showFileName;
        set => SetField(ref _showFileName, value);
    }

    public bool ShowLength
    {
        get => _showLength;
        set => SetField(ref _showLength, value);
    }

    public bool ShowFormat
    {
        get => _showFormat;
        set => SetField(ref _showFormat, value);
    }

    public bool ShowTrackNumber
    {
        get => _showTrackNumber;
        set => SetField(ref _showTrackNumber, value);
    }

    public bool ShowDiscNumber
    {
        get => _showDiscNumber;
        set => SetField(ref _showDiscNumber, value);
    }

    public bool ShowYear
    {
        get => _showYear;
        set => SetField(ref _showYear, value);
    }

    public bool ShowGenre
    {
        get => _showGenre;
        set => SetField(ref _showGenre, value);
    }

    public bool ShowBitrate
    {
        get => _showBitrate;
        set => SetField(ref _showBitrate, value);
    }

    public ObservableCollection<LibraryNode> LibraryRoots => _libraryRoots;
    public ObservableCollection<TrackRow> Tracks => _tracks;
    public ObservableCollection<QueueItem> Queue => _queue;

    public MainWindowViewModel()
    {
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrpheusMP",
            "library.db");

        _musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        _library = new SqliteMediaLibrary(_databasePath);
        _scanner = new FolderScanner(_library, new TagLibMetadataReader());
        _scanner.Progress += OnScanProgress;

        _player = new VlcPlayer("--no-video");
        _controller = new PlayerController(_player);
        _controller.Playlist.Changed += OnPlaylistChanged;
        _controller.Playlist.CurrentIndexChanged += OnPlaylistIndexChanged;
        _controller.ShufflePlayChanged += OnShufflePlayChanged;
        _controller.RepeatModeChanged += OnRepeatModeChanged;

        _player.PositionChanged += OnPositionChanged;
        _player.StateChanged += OnStateChanged;

        _player.Volume = (int)Math.Round(_volume);

        _ = InitializeAsync();
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        await _library.AddWatchedFolderAsync(folder);
        await _scanner.ScanFoldersAsync(new[] { folder });
    }

    private async Task InitializeAsync()
    {
        await EnsureDefaultLibraryAsync();
        await LoadLibraryAsync();
        await LoadPlaylistAsync();
    }

    private async Task EnsureDefaultLibraryAsync()
    {
        var watched = await _library.GetWatchedFoldersAsync();
        if (watched.Count == 0 && Directory.Exists(_musicFolder))
        {
            await _library.AddWatchedFolderAsync(_musicFolder);
            await _scanner.ScanAsync();
        }
    }

    private async Task LoadLibraryAsync(string? query = null)
    {
        IReadOnlyList<LibraryTrack> tracks;

        if (!string.IsNullOrWhiteSpace(query))
        {
            tracks = await _library.SearchAsync(query);
        }
        else
        {
            tracks = await _library.GetAllTracksAsync();
        }
        var folders = await _library.GetWatchedFoldersAsync();
        var treeTracks = !string.IsNullOrWhiteSpace(query)
            ? await _library.GetAllTracksAsync()
            : tracks;

        var totalTracks = tracks.Count;
        var treeNodes = await Task.Run(() => BuildFolderTree(folders, treeTracks));

        _allTracks = treeTracks;
        _currentTracks = tracks;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _libraryRoots.Clear();
            foreach (var node in treeNodes)
                _libraryRoots.Add(node);

            LibrarySummary = $"{folders.Count} folders - {totalTracks} tracks";
        });

        await RefreshTrackViewAsync(resetSelection: true);
    }

    private async Task LoadPlaylistAsync()
    {
        var tracks = await _library.GetAllTracksAsync();
        var items = tracks
            .Take(25)
            .Select(t => new PlaylistItem
            {
                Source = MediaSource.FromFile(t.FilePath),
                Metadata = new TrackMetadata
                {
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = t.Album,
                    Duration = t.Duration
                }
            })
            .ToList();

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);

        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
    }

    private void UpdateQueueFromPlaylist()
    {
        _queue.Clear();
        foreach (var item in _controller.Playlist)
        {
            _queue.Add(ToQueueItem(item));
        }

        QueueSummary = $"{_queue.Count} queued";
    }

    private void OnScanProgress(object? sender, LibraryScanProgress e)
    {
        if (e.IsComplete)
        {
            _ = LoadLibraryAsync();
            _ = LoadPlaylistAsync();
        }
    }

    private void OnPlaylistChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateQueueFromPlaylist);
    }

    private void OnPlaylistIndexChanged(object? sender, int index)
    {
        CurrentQueueIndex = index;
        var item = _controller.Playlist.CurrentItem;
        if (item is null)
            return;

        NowPlayingTitle = item.Metadata?.Title ?? item.DisplayName;
        NowPlayingArtist = item.Metadata?.Artist ?? string.Empty;
        NowPlayingAlbum = item.Metadata?.Album ?? string.Empty;
        UpdateNowPlayingTime(_player.Position, _player.Duration ?? TimeSpan.Zero);
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        var duration = _player.Duration ?? TimeSpan.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            PlaybackPosition = position.TotalSeconds;
            PlaybackDuration = duration.TotalSeconds;
            UpdateNowPlayingTime(position, duration);
        });
    }

    private void OnStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        var duration = _player.Duration ?? TimeSpan.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            PlaybackDuration = duration.TotalSeconds;
            PlaybackPosition = _player.Position.TotalSeconds;
            UpdateNowPlayingTime(_player.Position, duration);
        });
    }

    private void UpdateNowPlayingTime(TimeSpan position, TimeSpan duration)
    {
        NowPlayingTime = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString("h\\:mm\\:ss");
        return value.ToString("m\\:ss");
    }

    private static TrackRow ToTrackRow(LibraryTrack track)
    {
        return new TrackRow(
            track.Title ?? Path.GetFileNameWithoutExtension(track.FilePath),
            track.Artist ?? "",
            track.Album ?? "",
            Path.GetFileName(track.FilePath),
            track.TrackNumber?.ToString() ?? "",
            track.DiscNumber?.ToString() ?? "",
            track.Year?.ToString() ?? "",
            track.Genre ?? "",
            track.Bitrate is null ? "" : $"{track.Bitrate} kbps",
            FormatTime(track.Duration ?? TimeSpan.Zero),
            track.Codec ?? "");
    }

    private static QueueItem ToQueueItem(PlaylistItem item)
    {
        var metadata = item.Metadata;
        return new QueueItem(
            metadata?.Title ?? item.DisplayName,
            metadata?.Artist ?? "",
            FormatTime(metadata?.Duration ?? TimeSpan.Zero));
    }

    private static List<LibraryNode> BuildFolderTree(
        IReadOnlyList<string> folders,
        IReadOnlyList<LibraryTrack> tracks)
    {
        var counts = BuildTrackCounts(tracks);
        var nodes = new List<LibraryNode>();

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(BuildFolderNode(folder, counts));
        }

        return nodes;
    }

    private static Dictionary<string, int> BuildTrackCounts(IEnumerable<LibraryTrack> tracks)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            var dir = Path.GetDirectoryName(track.FilePath);
            while (!string.IsNullOrWhiteSpace(dir))
            {
                counts[dir] = counts.GetValueOrDefault(dir) + 1;
                dir = Path.GetDirectoryName(dir);
            }
        }

        return counts;
    }

    private static LibraryNode BuildFolderNode(string path, Dictionary<string, int> counts)
    {
        var children = new List<LibraryNode>();
        if (Directory.Exists(path))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    children.Add(BuildFolderNode(child, counts));
                }
            }
            catch
            {
            }
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        var count = counts.TryGetValue(path, out var value) ? value : 0;
        var meta = Directory.Exists(path)
            ? (count > 0 ? $"{count} tracks" : "Empty")
            : "Missing";

        return new LibraryNode(name, meta, path, children);
    }

    public async Task TogglePlayPauseAsync()
    {
        await _controller.TogglePlayPauseAsync().ConfigureAwait(false);
    }

    public async Task PlayPreviousAsync()
    {
        await _controller.PreviousAsync().ConfigureAwait(false);
    }

    public async Task PlayNextAsync()
    {
        await _controller.NextAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _controller.StopAsync().ConfigureAwait(false);
    }

    public void ToggleShuffle()
    {
        IsShuffleEnabled = _controller.ToggleShufflePlay();
    }

    public void CycleRepeat()
    {
        RepeatMode = _controller.CycleRepeatMode();
    }

    public async Task PlaySelectedAsync()
    {
        if (_selectedTrackIndex < 0)
            return;

        var viewTracks = _viewTracks.Count > 0 ? _viewTracks : _currentTracks;

        if (_selectedTrackIndex >= viewTracks.Count)
            return;

        var selected = viewTracks[_selectedTrackIndex];
        var (items, selectedIndex) = await Task.Run(() =>
            BuildPlaylistItems(viewTracks, selected.FilePath));

        if (items.Count == 0)
            return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);

        if (selectedIndex < 0)
            selectedIndex = 0;

        await _controller.PlayAtIndexAsync(selectedIndex).ConfigureAwait(false);
    }

    public async Task SelectFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        var filteredTracks = await Task.Run(() =>
            _allTracks.Where(t => IsUnderFolder(t.FilePath, folderPath)).ToList());

        _currentTracks = filteredTracks;
        await RefreshTrackViewAsync(resetSelection: true);
    }

    public async Task PlayFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        var folderTracks = await Task.Run(() =>
            _allTracks.Where(t => IsUnderFolder(t.FilePath, folderPath)).ToList());

        var (items, _) = await Task.Run(() => BuildPlaylistItems(folderTracks, null));
        if (items.Count == 0)
            return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        var folderFull = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fileFull = Path.GetFullPath(filePath);
        return fileFull.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase);
    }

    private static (List<PlaylistItem> Items, int SelectedIndex) BuildPlaylistItems(
        IReadOnlyList<LibraryTrack> tracks,
        string? selectedPath)
    {
        var items = new List<PlaylistItem>();
        var selectedIndex = -1;

        foreach (var track in tracks)
        {
            try
            {
                var item = new PlaylistItem
                {
                    Source = MediaSource.FromFile(track.FilePath),
                    Metadata = new TrackMetadata
                    {
                        Title = track.Title,
                        Artist = track.Artist,
                        Album = track.Album,
                        Duration = track.Duration
                    }
                };

                items.Add(item);
                if (!string.IsNullOrWhiteSpace(selectedPath) &&
                    string.Equals(track.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = items.Count - 1;
                }
            }
            catch
            {
            }
        }

        return (items, selectedIndex);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _scanner.Progress -= OnScanProgress;
        _controller.Playlist.Changed -= OnPlaylistChanged;
        _controller.Playlist.CurrentIndexChanged -= OnPlaylistIndexChanged;
        _controller.ShufflePlayChanged -= OnShufflePlayChanged;
        _controller.RepeatModeChanged -= OnRepeatModeChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.StateChanged -= OnStateChanged;
        await _controller.DisposeAsync().ConfigureAwait(false);
        _library.Dispose();
    }

    private async Task RefreshTrackViewAsync(bool resetSelection = false)
    {
        var view = await Task.Run(() => BuildTrackView(_currentTracks));
        _viewTracks = view.Tracks;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _tracks.Clear();
            foreach (var row in view.Rows)
                _tracks.Add(row);

            if (resetSelection || SelectedTrackIndex >= _tracks.Count)
                SelectedTrackIndex = -1;
        });
    }

    private TrackView BuildTrackView(IReadOnlyList<LibraryTrack> source)
    {
        IEnumerable<LibraryTrack> query = source;

        if (HideMissingTitle)
            query = query.Where(t => !string.IsNullOrWhiteSpace(t.Title));
        if (HideMissingArtist)
            query = query.Where(t => !string.IsNullOrWhiteSpace(t.Artist));
        if (HideMissingAlbum)
            query = query.Where(t => !string.IsNullOrWhiteSpace(t.Album));
        if (HideMissingGenre)
            query = query.Where(t => !string.IsNullOrWhiteSpace(t.Genre));
        if (HideMissingTrackNumber)
            query = query.Where(t => t.TrackNumber.HasValue && t.TrackNumber.Value > 0);

        query = ApplySort(query);

        var list = query.ToList();
        var rows = list.Select(ToTrackRow).ToList();
        return new TrackView(list, rows);
    }

    private IEnumerable<LibraryTrack> ApplySort(IEnumerable<LibraryTrack> tracks)
    {
        return SortField switch
        {
            TrackSortField.Title => OrderByString(tracks, t => t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath), SortAscending),
            TrackSortField.Artist => OrderByString(tracks, t => t.Artist, SortAscending),
            TrackSortField.Album => OrderByString(tracks, t => t.Album, SortAscending),
            TrackSortField.FileName => OrderByString(tracks, t => Path.GetFileName(t.FilePath), SortAscending),
            TrackSortField.TrackNumber => OrderByNumber(tracks, t => t.TrackNumber ?? uint.MaxValue, SortAscending),
            TrackSortField.Year => OrderByNumber(tracks, t => t.Year ?? uint.MaxValue, SortAscending),
            TrackSortField.Duration => OrderByNumber(tracks, t => t.DurationMs ?? long.MaxValue, SortAscending),
            TrackSortField.DateAdded => OrderByNumber(tracks, t => t.DateAddedTicks, SortAscending),
            TrackSortField.Bitrate => OrderByNumber(tracks, t => t.Bitrate ?? int.MaxValue, SortAscending),
            _ => OrderByString(tracks, t => t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath), SortAscending)
        };
    }

    private static IEnumerable<LibraryTrack> OrderByString(
        IEnumerable<LibraryTrack> tracks,
        Func<LibraryTrack, string?> selector,
        bool ascending)
    {
        return ascending
            ? tracks.OrderBy(t => selector(t) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : tracks.OrderByDescending(t => selector(t) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<LibraryTrack> OrderByNumber<T>(
        IEnumerable<LibraryTrack> tracks,
        Func<LibraryTrack, T> selector,
        bool ascending) where T : IComparable<T>
    {
        return ascending
            ? tracks.OrderBy(selector)
            : tracks.OrderByDescending(selector);
    }

    private void OnShufflePlayChanged(object? sender, bool enabled)
    {
        Dispatcher.UIThread.Post(() => IsShuffleEnabled = enabled);
    }

    private void OnRepeatModeChanged(object? sender, RepeatMode mode)
    {
        Dispatcher.UIThread.Post(() => RepeatMode = mode);
    }

    public void SetSortField(TrackSortField field)
    {
        if (SortField == field)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortField = field;
            SortAscending = true;
        }

        _ = RefreshTrackViewAsync();
    }

    public void ToggleSortDirection()
    {
        SortAscending = !SortAscending;
        _ = RefreshTrackViewAsync();
    }
}

public sealed record LibraryNode(string Name, string Meta, string Path, IReadOnlyList<LibraryNode>? Children = null);

public sealed record TrackRow(
    string Title,
    string Artist,
    string Album,
    string FileName,
    string TrackNumber,
    string DiscNumber,
    string Year,
    string Genre,
    string Bitrate,
    string Length,
    string Format);

public sealed record QueueItem(string Title, string Artist, string Length);

public readonly record struct TrackView(IReadOnlyList<LibraryTrack> Tracks, IReadOnlyList<TrackRow> Rows);

public enum TrackSortField
{
    Title,
    Artist,
    Album,
    FileName,
    TrackNumber,
    Year,
    Duration,
    DateAdded,
    Bitrate
}

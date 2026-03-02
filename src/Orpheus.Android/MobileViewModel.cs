using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Orpheus.Core.Library;
using Orpheus.Core.Metadata;
using Orpheus.Core.Playback;
using Orpheus.Core.Playlist;
using Orpheus.Core.Media;

namespace Orpheus.Android;

public sealed class MobileViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMediaLibrary _library;
    private readonly FolderScanner _scanner;
    private readonly PlayerController _controller;

    private string _nowPlayingTitle = "Nothing Playing";
    private string _nowPlayingArtist = "";
    private string _nowPlayingAlbum = "";
    private string _nowPlayingTime = "00:00 / 00:00";
    private double _playbackDuration;
    private double _playbackPosition;
    private double _volume = 72;
    private bool _isPlaying;
    private bool _isActive;
    private bool _isShuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private bool _isMuted;

    private readonly ObservableCollection<QueueItem> _queue = new();
    private readonly ObservableCollection<LibraryNode> _libraryRoots = new();

    public event PropertyChangedEventHandler? PropertyChanged;

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
        set
        {
            if (SetField(ref _playbackPosition, value))
            {
                _ = _controller.SeekAsync(TimeSpan.FromSeconds(value));
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetField(ref _volume, value))
            {
                _controller.SetVolume(value);
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetField(ref _isPlaying, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIcon)));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatIcon)));
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                _controller.ToggleMute();
            }
        }
    }

    public IImage? PlayPauseIcon => SvgIconHelper.Load(
        _isPlaying
            ? "avares://Orpheus.Android/assets/icons/pause.svg"
            : "avares://Orpheus.Android/assets/icons/play.svg",
        ResolveIconColor());

    public IImage? PreviousIcon => SvgIconHelper.Load(
        "avares://Orpheus.Android/assets/icons/previous.svg", ResolveIconColor());

    public IImage? NextIcon => SvgIconHelper.Load(
        "avares://Orpheus.Android/assets/icons/next.svg", ResolveIconColor());

    public IImage? ShuffleIcon => SvgIconHelper.Load(
        "avares://Orpheus.Android/assets/icons/shuffle.svg",
        _isShuffleEnabled ? ResolveIconActiveColor() : ResolveIconColor());

    public IImage? RepeatIcon
    {
        get
        {
            var path = _repeatMode switch
            {
                RepeatMode.All => "avares://Orpheus.Android/assets/icons/repeat-all.svg",
                RepeatMode.One => "avares://Orpheus.Android/assets/icons/repeat-one.svg",
                _ => "avares://Orpheus.Android/assets/icons/repeat-none.svg",
            };
            return SvgIconHelper.Load(path, _repeatMode != RepeatMode.Off ? ResolveIconActiveColor() : ResolveIconColor());
        }
    }

    public IImage? VolumeIcon => SvgIconHelper.Load(
        _volume >= 35
            ? "avares://Orpheus.Android/assets/icons/volume-high.svg"
            : "avares://Orpheus.Android/assets/icons/volume-low.svg",
        _isMuted ? Color.Parse("#666666") : ResolveIconColor());

    public ObservableCollection<QueueItem> Queue => _queue;
    public ObservableCollection<LibraryNode> LibraryRoots => _libraryRoots;

    public MobileViewModel()
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrpheusMP",
            "library.db");

        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        _library = new SqliteMediaLibrary(databasePath);
        _scanner = new FolderScanner(_library, new TagLibMetadataReader());

        var player = new VlcPlayer();
        _controller = new PlayerController(player, null, _volume);
        _controller.Playlist.CurrentIndexChanged += OnPlaylistIndexChanged;
        _controller.StateChanged += OnControllerStateChanged;
        _controller.PositionChanged += OnControllerPositionChanged;

        _ = InitializeAsync(musicFolder);
    }

    private async Task InitializeAsync(string musicFolder)
    {
        var watched = await _library.GetWatchedFoldersAsync();
        if (watched.Count == 0 && Directory.Exists(musicFolder))
        {
            await _library.AddWatchedFolderAsync(musicFolder);
            await _scanner.ScanAsync();
        }

        await LoadLibraryAsync();
        UpdateQueueFromPlaylist();
    }

    private async Task LoadLibraryAsync()
    {
        var tracks = await _library.GetAllTracksAsync();
        var folders = await _library.GetWatchedFoldersAsync();

        var treeNodes = await Task.Run(() => BuildFolderTree(folders, tracks));

        _libraryRoots.Clear();
        foreach (var node in treeNodes)
            _libraryRoots.Add(node);
    }

    private static System.Collections.Generic.List<LibraryNode> BuildFolderTree(
        System.Collections.Generic.IReadOnlyList<string> folders,
        System.Collections.Generic.IReadOnlyList<LibraryTrack> tracks)
    {
        var nodes = new System.Collections.Generic.List<LibraryNode>();
        foreach (var folder in folders)
        {
            var node = BuildFolderNode(folder, tracks);
            if (node is not null)
            {
                node.IsExpanded = true;
                nodes.Add(node);
            }
        }
        return nodes;
    }

    private static LibraryNode? BuildFolderNode(string path, System.Collections.Generic.IReadOnlyList<LibraryTrack> tracks)
    {
        var children = new System.Collections.Generic.List<LibraryNode>();
        if (Directory.Exists(path))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    children.Add(BuildFolderNode(child, tracks)!);
                }
            }
            catch { }
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        return new LibraryNode(name, "", path, children);
    }

    private void OnPlaylistIndexChanged(object? sender, int index)
    {
        var item = _controller.Playlist.CurrentItem;
        if (item is null) return;

        NowPlayingTitle = item.Metadata?.Title ?? item.DisplayName;
        NowPlayingArtist = item.Metadata?.Artist ?? "";
        NowPlayingAlbum = item.Metadata?.Album ?? "";
    }

    private void OnControllerStateChanged(object? sender, PlaybackStateSnapshot snap)
    {
        IsPlaying = snap.IsPlaying;
        IsActive = !snap.IsStopped;
        PlaybackDuration = snap.Duration.TotalSeconds;
        PlaybackPosition = snap.Position.TotalSeconds;
        UpdateNowPlayingTime(snap.Position, snap.Duration);
    }

    private void OnControllerPositionChanged(object? sender, PositionSnapshot snap)
    {
        PlaybackPosition = snap.Position.TotalSeconds;
        PlaybackDuration = snap.Duration.TotalSeconds;
        UpdateNowPlayingTime(snap.Position, snap.Duration);
    }

    private void UpdateNowPlayingTime(TimeSpan position, TimeSpan duration)
    {
        NowPlayingTime = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString("h\\:mm\\:ss")
            : value.ToString("m\\:ss");
    }

    private void UpdateQueueFromPlaylist()
    {
        _queue.Clear();
        foreach (var item in _controller.Playlist)
        {
            _queue.Add(new QueueItem(
                item.Metadata?.Title ?? item.DisplayName,
                item.Metadata?.Artist ?? "",
                FormatTime(item.Metadata?.Duration ?? TimeSpan.Zero)));
        }
    }

    private Color ResolveIconColor() => Color.Parse("#D4A843");
    private Color ResolveIconActiveColor() => Colors.White;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_controller.Playlist.CurrentIndex < 0 && _queue.Count > 0)
        {
            await PlayQueueIndexAsync(0);
            return;
        }
        await _controller.TogglePlayPauseAsync();
    }

    public async Task PlayNextAsync() => await _controller.PlayAtIndexAsync(_controller.Playlist.CurrentIndex + 1);
    public async Task PlayPreviousAsync() => await _controller.PlayAtIndexAsync(_controller.Playlist.CurrentIndex - 1);
    public async Task StopAsync() => await _controller.StopAsync();

    public async Task ToggleShuffleAsync()
    {
        _controller.ToggleShufflePlay();
        _isShuffleEnabled = !_isShuffleEnabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShuffleIcon)));
    }

    public async Task ToggleRepeatAsync()
    {
        _controller.CycleRepeatMode();
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatIcon)));
    }

    public async Task PlayQueueIndexAsync(int index)
    {
        if (index >= 0 && index < _queue.Count)
        {
            await _controller.PlayAtIndexAsync(index);
        }
    }

    public async Task AddTrackToQueueAsync(LibraryTrack track)
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
        _controller.Playlist.Add(item);
        UpdateQueueFromPlaylist();
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
    }
}

public sealed class LibraryNode : INotifyPropertyChanged
{
    public string Name { get; }
    public string Meta { get; set; }
    public string Path { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<LibraryNode> Children { get; }

    public LibraryNode(string name, string meta, string path, System.Collections.Generic.IList<LibraryNode>? children = null)
    {
        Name = name;
        Meta = meta;
        Path = path;
        Children = children != null ? new ObservableCollection<LibraryNode>(children) : new ObservableCollection<LibraryNode>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record QueueItem(string Primary, string Secondary, string Duration);

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Orpheus.Core.Library;
using Orpheus.Core.Metadata;
using Orpheus.Core.Playback;
using Orpheus.Core.Playlist;
using Orpheus.Core.Media;

namespace Orpheus.Android;

// ── Tab enum ──────────────────────────────────────────────────────────
public enum MobileTab { Library, Queue }

// ── Track display mode ────────────────────────────────────────────────
public enum QueueDisplayMode
{
    /// <summary>Show metadata Title as primary, Album as secondary.</summary>
    TitleAlbum,
    /// <summary>Show file name (no ext) as primary, parent folder as secondary.</summary>
    FileNameFolder,
    /// <summary>Title+Album when metadata is present, falls back to FileName+Folder.</summary>
    TitleAlbumWithFallback,
}

// ── Main ViewModel ────────────────────────────────────────────────────
public sealed class MobileViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMediaLibrary _library;
    private readonly FolderScanner _scanner;
    private readonly PlayerController _controller;

    // ── Playback state ────────────────────────────────────────────────
    private string _nowPlayingTitle  = "Nothing Playing";
    private string _nowPlayingArtist = "";
    private string _nowPlayingAlbum  = "";
    private string _nowPlayingTime   = "0:00 / 0:00";
    private double _playbackDuration;
    private double _playbackPosition;
    private bool   _isUserSeekingPosition;
    private double _volume = 72;
    private bool   _isPlaying;
    private bool   _isActive;
    private bool   _isShuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private bool   _isMuted;
    private int    _currentQueueIndex = -1;

    // ── Navigation ────────────────────────────────────────────────────
    private MobileTab _activeTab = MobileTab.Library;

    // ── Library navigation stack ──────────────────────────────────────
    // Each entry is the LibraryNode currently displayed in the library tab.
    // Pushing drills in; popping goes back.
    private readonly Stack<LibraryNode> _navStack = new();
    private LibraryNode? _currentNode;

    // ── Watched folders (shown at root level for management) ────────────
    private readonly ObservableCollection<string> _watchedFolders = new();

    // ── Status ────────────────────────────────────────────────────────
    private string _statusMessage = "";
    private bool   _isScanning;
    private string _librarySummary = "";

    // ── Collections ───────────────────────────────────────────────────
    // Queue is a plain list — rebuilt in bulk and exposed via a property-change
    // notification so Avalonia replaces the ItemsSource in one pass instead of
    // firing CollectionChanged once per item (catastrophic at 6000+ tracks).
    private List<QueueItem> _queue = new();
    private readonly ObservableCollection<LibraryNode> _libraryRoots = new();
    // The items shown in the library tab body (either root nodes or the
    // sub-folders + tracks of the current navigation node).
    private readonly ObservableCollection<LibraryNode> _displayedNodes = new();
    // Tracks shown when a folder node is selected
    private readonly ObservableCollection<TrackRow>    _displayedTracks = new();

    // ── All library tracks cache (for folder browsing) ───────────────
    private IReadOnlyList<LibraryTrack> _allTracks = Array.Empty<LibraryTrack>();

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Playback properties ───────────────────────────────────────────

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (SetField(ref _nowPlayingTitle, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowPlayingLabel)));
        }
    }

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set
        {
            if (SetField(ref _nowPlayingArtist, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowPlayingLabel)));
        }
    }

    public string NowPlayingAlbum
    {
        get => _nowPlayingAlbum;
        private set => SetField(ref _nowPlayingAlbum, value);
    }

    /// <summary>
    /// The local filesystem path of the currently playing track, or null when nothing is loaded.
    /// Used by <see cref="PlaybackService"/> to read album art for the media session.
    /// </summary>
    public string? CurrentFilePath
    {
        get
        {
            var item = _controller.Playlist.CurrentItem;
            if (item is null) return null;
            return item.Source.Type == Orpheus.Core.Media.MediaSourceType.LocalFile
                ? item.Source.Uri.LocalPath
                : null;
        }
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
            if (SetField(ref _playbackPosition, value) && _isUserSeekingPosition)
                _ = _controller.SeekAsync(TimeSpan.FromSeconds(value));
        }
    }

    public bool IsUserSeekingPosition
    {
        get => _isUserSeekingPosition;
        set => SetField(ref _isUserSeekingPosition, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetField(ref _volume, value))
            {
                _controller.SetVolume(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
                ScheduleStateSave();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetField(ref _isPlaying, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIcon)));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetField(ref _isActive, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowPlayingLabel)));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatIcon)));
        }
    }

    public bool RepeatModeIsActive => _repeatMode != RepeatMode.Off;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                _controller.ToggleMute();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
            }
        }
    }

    public int CurrentQueueIndex
    {
        get => _currentQueueIndex;
        private set => SetField(ref _currentQueueIndex, value);
    }

    // ── Tab navigation ────────────────────────────────────────────────

    public MobileTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetField(ref _activeTab, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryTabActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQueueTabActive)));
            }
        }
    }

    public bool IsLibraryTabActive => _activeTab == MobileTab.Library;
    public bool IsQueueTabActive   => _activeTab == MobileTab.Queue;

    // ── Settings overlay ──────────────────────────────────────────────

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetField(ref _isSettingsOpen, value);
    }

    public IImage? SettingsIcon => LoadIcon("settings");

    // ── Track display mode ────────────────────────────────────────────

    private QueueDisplayMode _trackDisplayMode = QueueDisplayMode.TitleAlbum;

    public QueueDisplayMode TrackDisplayMode
    {
        get => _trackDisplayMode;
        set
        {
            if (!SetField(ref _trackDisplayMode, value)) return;
            // Refresh the transport label and the queue list
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowPlayingLabel)));
            UpdateQueueDisplay();
            // Persist
            var config = MobileConfig.Load();
            config.TrackDisplayMode = value.ToString();
            config.Save();
        }
    }

    // ── Library navigation ────────────────────────────────────────────

    public bool CanNavigateBack => _navStack.Count > 0;

    public string CurrentFolderName =>
        _currentNode is not null ? _currentNode.Name : "Library";

    // ── Status ────────────────────────────────────────────────────────

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetField(ref _isScanning, value);
    }

    public string LibrarySummary
    {
        get => _librarySummary;
        private set => SetField(ref _librarySummary, value);
    }

    // ── Collections ───────────────────────────────────────────────────

    public List<QueueItem>                   Queue           => _queue;
    public ObservableCollection<LibraryNode> LibraryRoots   => _libraryRoots;
    public ObservableCollection<LibraryNode> DisplayedNodes  => _displayedNodes;
    public ObservableCollection<TrackRow>    DisplayedTracks => _displayedTracks;
    public ObservableCollection<string>      WatchedFolders  => _watchedFolders;

    public string QueueSummary =>
        _queue.Count == 0 ? "Queue is empty" : $"{_queue.Count} track{(_queue.Count == 1 ? "" : "s")}";

    public bool IsLibraryEmpty =>
        _displayedNodes.Count == 0 && _displayedTracks.Count == 0 && !_isScanning;

    /// <summary>Primary + secondary display text shown above the seek bar in the transport footer.</summary>
    public string NowPlayingLabel
    {
        get
        {
            if (!_isActive) return "";
            if (!string.IsNullOrEmpty(_nowPlayingArtist))
                return $"{_nowPlayingTitle}  ·  {_nowPlayingArtist}";
            return _nowPlayingTitle;
        }
    }

    // ── Icon properties ───────────────────────────────────────────────

    public IImage? PlayPauseIcon => LoadIcon(
        _isPlaying ? "pause" : "play",
        active: false);

    public IImage? PreviousIcon  => LoadIcon("previous");
    public IImage? NextIcon      => LoadIcon("next");
    public IImage? StopIcon      => LoadIcon("stop");
    public IImage? AddFolderIcon => LoadIcon("plus");

    public IImage? ShuffleIcon => LoadIcon("shuffle", active: _isShuffleEnabled);
    public IImage? RepeatIcon
    {
        get
        {
            var name = _repeatMode switch
            {
                RepeatMode.All => "repeat-all",
                RepeatMode.One => "repeat-one",
                _              => "repeat-none",
            };
            return LoadIcon(name, active: _repeatMode != RepeatMode.Off);
        }
    }

    public IImage? VolumeIcon => LoadIcon(
        _volume >= 35 ? "volume-high" : "volume-low",
        active: false,
        muted: _isMuted);

    public IImage? BackIcon   => LoadIcon("previous");  // reuse prev chevron for back
    public IImage? SearchIcon => LoadIcon("search");

    // ── Constructor ───────────────────────────────────────────────────

    public MobileViewModel()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrpheusMP");

        Directory.CreateDirectory(dataDir);

        var databasePath = Path.Combine(dataDir, "library.db");

        _library = new SqliteMediaLibrary(databasePath);
        _scanner = new FolderScanner(_library, new TagLibMetadataReader());

        // Android: libvlc.so is already loaded by the runtime. We pre-set the
        // internal _libvlcLoaded flag via reflection so EnsureLoaded() is a no-op.
        var coreType   = typeof(LibVLCSharp.Shared.Core);
        var loadedField = coreType.GetField("_libvlcLoaded",
            BindingFlags.NonPublic | BindingFlags.Static);
        loadedField?.SetValue(null, true);

        var state = MobileState.Load();
        _volume = state.Volume;

        var config = MobileConfig.Load();
        if (Enum.TryParse<QueueDisplayMode>(config.TrackDisplayMode, out var dm))
            _trackDisplayMode = dm;

        var player = new VlcPlayer(initializeCore: static () => { });
        _controller = new PlayerController(player, state.AudioDevice, _volume);
        _controller.Playlist.CurrentIndexChanged += OnPlaylistIndexChanged;
        _controller.StateChanged  += OnControllerStateChanged;
        _controller.PositionChanged += OnControllerPositionChanged;

        _ = InitializeAsync(state);
    }

    // ── Initialization ────────────────────────────────────────────────

    // Saved state kept so the post-permission phase can restore the queue.
    private MobileState? _pendingState;

    private async Task InitializeAsync(MobileState state)
    {
        _pendingState = state;

        // Load whatever is already in the database (may be empty on first run).
        // We do NOT scan here — scanning requires storage permission which may
        // not have been granted yet. MainActivity calls GrantStoragePermissionAsync()
        // after the system permission dialog is resolved.
        await LoadLibraryAsync();

        // Restore previous queue (tracks already in the DB can be loaded without
        // extra storage permission since we only need the file path, not to enumerate
        // the filesystem).
        if (state.QueuePaths.Count > 0)
            await RestoreQueueAsync(state.QueuePaths, state.QueueIndex);
    }

    /// <summary>
    /// Called by MainActivity once the user has granted (or already held) storage
    /// read permission.  Performs the first-run folder scan if no folders have been
    /// watched yet, otherwise does nothing (the library was already loaded in
    /// <see cref="InitializeAsync"/>).
    /// </summary>
    public async Task GrantStoragePermissionAsync()
    {
        var musicFolder = GetDefaultMusicFolder();
        var watched = await _library.GetWatchedFoldersAsync();

        if (watched.Count == 0 && Directory.Exists(musicFolder))
        {
            await _library.AddWatchedFolderAsync(musicFolder);
            await ScanFoldersAsync([musicFolder]);
        }
        // If folders are already watched the library was loaded in InitializeAsync;
        // nothing more to do.
    }

    private static string GetDefaultMusicFolder()
    {
        // Android shared storage Music folder
        var emulated = "/storage/emulated/0/Music";
        if (Directory.Exists(emulated)) return emulated;

        // Fallback: SpecialFolder.MyMusic (works on desktop for dev builds)
        var special = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrEmpty(special)) return special;

        return emulated; // let the caller check Directory.Exists
    }

    // Scan all watched folders (e.g. on rescan)
    public async Task ScanAsync()
    {
        IsScanning = true;
        StatusMessage = "Scanning…";
        try
        {
            await _scanner.ScanAsync();
            await LoadLibraryAsync();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    // Scan only the given folders (used on add, to avoid re-scanning everything)
    private async Task ScanFoldersAsync(IEnumerable<string> folders)
    {
        IsScanning = true;
        StatusMessage = "Scanning…";
        try
        {
            await _scanner.ScanFoldersAsync(folders);
            await LoadLibraryAsync();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task LoadLibraryAsync()
    {
        _allTracks = await _library.GetAllTracksAsync();
        var folders = await _library.GetWatchedFoldersAsync();

        var roots = await Task.Run(() => BuildFolderTree(folders, _allTracks));

        _libraryRoots.Clear();
        foreach (var n in roots) _libraryRoots.Add(n);

        _watchedFolders.Clear();
        foreach (var f in folders) _watchedFolders.Add(f);

        // Reset navigation
        _navStack.Clear();
        _currentNode = null;
        NotifyNavChanged();
        ShowRootNodes();

        var trackCount  = _allTracks.Count;
        var folderCount = folders.Count;
        LibrarySummary  = $"{folderCount} folder{(folderCount == 1 ? "" : "s")}, {trackCount} track{(trackCount == 1 ? "" : "s")}";
    }

    private void ShowRootNodes()
    {
        _displayedNodes.Clear();
        foreach (var n in _libraryRoots) _displayedNodes.Add(n);
        _displayedTracks.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryEmpty)));
    }

    // ── Library navigation ─────────────────────────────────────────────

    /// <summary>
    /// Drill into a folder node: push current on stack, display its children + tracks.
    /// </summary>
    public void NavigateInto(LibraryNode node)
    {
        if (_currentNode is not null)
            _navStack.Push(_currentNode);
        else
            _navStack.Push(new LibraryNode("__root__", "", "__root__"));

        _currentNode = node;
        NotifyNavChanged();
        ShowNodeContents(node);
    }

    /// <summary>
    /// Navigate up one level.
    /// </summary>
    public void NavigateBack()
    {
        if (_navStack.Count == 0) return;
        var parent = _navStack.Pop();

        if (parent.Path == "__root__")
        {
            _currentNode = null;
            NotifyNavChanged();
            ShowRootNodes();
        }
        else
        {
            _currentNode = parent;
            NotifyNavChanged();
            ShowNodeContents(parent);
        }
    }

    private void ShowNodeContents(LibraryNode node)
    {
        _displayedNodes.Clear();
        foreach (var child in node.Children) _displayedNodes.Add(child);

        _displayedTracks.Clear();
        if (Directory.Exists(node.Path))
        {
            var tracks = _allTracks
                .Where(t => string.Equals(
                    Path.GetDirectoryName(t.FilePath),
                    node.Path,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.TrackNumber ?? 0)
                .ThenBy(t => t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath),
                    StringComparer.OrdinalIgnoreCase)
                .Select(t => new TrackRow(t))
                .ToList();

            foreach (var row in tracks) _displayedTracks.Add(row);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryEmpty)));
    }

    private void NotifyNavChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNavigateBack)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentFolderName)));
    }

    // ── Library folder management ─────────────────────────────────────

    /// <summary>
    /// Resolves an <see cref="Avalonia.Platform.Storage.IStorageFolder"/> to a real
    /// filesystem path on Android.
    ///
    /// Avalonia's StorageProvider wraps SAF URIs on Android, so
    /// <c>TryGetLocalPath()</c> always returns <c>null</c>.  For the common
    /// external-storage provider the tree URI looks like:
    ///   content://com.android.externalstorage.documents/tree/primary%3AMusic
    /// The document-tree ID ("primary:Music", "XXXX-XXXX:Podcasts", …) encodes
    /// the root and relative path:
    ///   primary  → /storage/emulated/0
    ///   XXXX-XXXX → /storage/XXXX-XXXX   (SD card)
    /// </summary>
    /// <summary>
    /// Resolves an <see cref="Avalonia.Platform.Storage.IStorageFile"/> to a real
    /// filesystem path on Android. SAF document URIs look like:
    ///   content://com.android.externalstorage.documents/document/primary%3AMusic%2FQueue.m3u
    /// The document ID encodes root:relative/path identically to tree URIs.
    /// </summary>
    public static string? ResolveStorageFilePath(Avalonia.Platform.Storage.IStorageFile file)
    {
        // Fast path: real filesystem URI (file://)
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;

        var uri = file.Path;
        if (uri is null) return null;

        // SAF document URI: /document/primary%3AMusic%2FQueue.m3u
        var segments = uri.AbsolutePath.Split('/');
        var docIdx   = Array.IndexOf(segments, "document");
        if (docIdx < 0 || docIdx + 1 >= segments.Length) return null;

        var docId  = Uri.UnescapeDataString(segments[docIdx + 1]);
        var colon  = docId.IndexOf(':');
        if (colon < 0) return null;

        var root = docId[..colon];
        var rel  = docId[(colon + 1)..].Replace('/', Path.DirectorySeparatorChar);

        var rootPath = root.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? "/storage/emulated/0"
            : $"/storage/{root}";

        return string.IsNullOrEmpty(rel)
            ? rootPath
            : Path.Combine(rootPath, rel);
    }

    public static string? ResolveStorageFolderPath(Avalonia.Platform.Storage.IStorageFolder folder)
    {
        // Fast path: real filesystem URI (file://)
        var local = folder.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;

        // Decode SAF tree URI
        var uri = folder.Path;
        if (uri is null) return null;

        // uri.AbsolutePath for a SAF tree URI is e.g.
        //   /tree/primary%3AMusic   or   /tree/1A2B-3C4D%3APodcasts
        var segments = uri.AbsolutePath.Split('/');
        // Find the segment after "tree"
        var treeIdx = Array.IndexOf(segments, "tree");
        if (treeIdx < 0 || treeIdx + 1 >= segments.Length) return null;

        var docId = Uri.UnescapeDataString(segments[treeIdx + 1]);
        // docId = "primary:Music" or "1A2B-3C4D:Podcasts"
        var colon = docId.IndexOf(':');
        if (colon < 0) return null;

        var root   = docId[..colon];
        var rel    = docId[(colon + 1)..].Replace('/', Path.DirectorySeparatorChar);

        var rootPath = root.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? "/storage/emulated/0"
            : $"/storage/{root}";

        return string.IsNullOrEmpty(rel)
            ? rootPath
            : Path.Combine(rootPath, rel);
    }

    public async Task AddFolderAsync(string path)
    {
        // Do not gate on Directory.Exists here: on Android 10+ with scoped storage
        // the path decoded from a SAF URI may not be accessible via the direct
        // filesystem API even though the user just granted access via the picker.
        // The FolderScanner already skips folders that don't exist, so it is safe
        // to proceed. Guarding here caused re-added folders to silently do nothing.
        await _library.AddWatchedFolderAsync(path);
        await ScanFoldersAsync([path]);   // scan only the newly added folder
    }

    public async Task RemoveFolderAsync(string path)
    {
        await _library.RemoveWatchedFolderAsync(path);
        await _library.RemoveTracksUnderFolderAsync(path);
        await LoadLibraryAsync();
    }

    public async Task ResetLibraryAsync()
    {
        await _library.ClearAsync();
        await LoadLibraryAsync();
    }

    public async Task RescanAsync() => await ScanAsync();

    // ── Playback commands ─────────────────────────────────────────────

    /// <summary>Seek to an absolute position in seconds. Safe to call from PointerReleased.</summary>
    public async Task SeekToPositionAsync(double seconds)
        => await _controller.SeekAsync(TimeSpan.FromSeconds(seconds));

    public async Task TogglePlayPauseAsync()
    {
        if (_controller.Playlist.CurrentIndex < 0 && _queue.Count > 0)
        {
            await PlayQueueIndexAsync(0);
            return;
        }
        await _controller.TogglePlayPauseAsync();
    }

    public async Task PlayNextAsync()
        => await _controller.PlayAtIndexAsync(_controller.Playlist.CurrentIndex + 1);

    public async Task PlayPreviousAsync()
        => await _controller.PlayAtIndexAsync(_controller.Playlist.CurrentIndex - 1);

    public async Task StopAsync()
        => await _controller.StopAsync();

    public async Task ToggleShuffleAsync()
    {
        _controller.ToggleShufflePlay();
        IsShuffleEnabled = !_isShuffleEnabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShuffleIcon)));
    }

    public async Task ToggleRepeatAsync()
    {
        _controller.CycleRepeatMode();
        RepeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _              => RepeatMode.Off,
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatModeIsActive)));
    }

    public async Task PlayQueueIndexAsync(int index)
    {
        if (index >= 0 && index < _queue.Count)
            await _controller.PlayAtIndexAsync(index);
    }

    public async Task RemoveFromQueueAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _controller.Playlist.RemoveAt(index);
        _queue = new List<QueueItem>(_queue);
        _queue.RemoveAt(index);
        NotifyQueueChanged();
        ScheduleStateSave();
    }

    public async Task ClearQueueAsync()
    {
        _controller.Playlist.Clear();
        _queue = new List<QueueItem>();
        NotifyQueueChanged();
        ScheduleStateSave();
    }

    /// <summary>Move a queue item from <paramref name="fromIndex"/> to <paramref name="toIndex"/>.</summary>
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _queue.Count) return;
        if (toIndex < 0 || toIndex >= _queue.Count) return;

        _controller.Playlist.Move(fromIndex, toIndex);

        var newQueue = new List<QueueItem>(_queue);
        var item = newQueue[fromIndex];
        newQueue.RemoveAt(fromIndex);
        newQueue.Insert(toIndex, item);
        _queue = newQueue;
        NotifyQueueChanged();
        ScheduleStateSave();
    }

    // ── Queue building from library ───────────────────────────────────

    /// <summary>
    /// Play a file directly by path (e.g. opened via a file manager intent).
    /// Looks up library metadata if available; falls back to a bare PlaylistItem.
    /// </summary>
    public async Task PlayFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        _controller.Playlist.Clear();
        _queue.Clear();

        var track = await _library.GetTrackByPathAsync(filePath);
        if (track is not null)
        {
            AddTrackToPlaylist(track);
        }
        else
        {
            _controller.Playlist.Add(new PlaylistItem
            {
                Source = MediaSource.FromFile(filePath),
            });
        }

        UpdateQueueDisplay();
        await _controller.PlayAtIndexAsync(0);
        ActiveTab = MobileTab.Queue;
        ScheduleStateSave();
    }

    /// <summary>Play a single track (replaces queue with just this track).</summary>
    public async Task PlayTrackAsync(TrackRow row)
    {
        _controller.Playlist.Clear();
        _queue.Clear();
        AddTrackToPlaylist(row.Track);
        UpdateQueueDisplay();
        await _controller.PlayAtIndexAsync(0);
        ActiveTab = MobileTab.Queue;
        ScheduleStateSave();
    }

    /// <summary>Load a playlist file and replace the queue, then start playing.</summary>
    public async Task LoadPlaylistAsync(LibraryNode node)
    {
        if (node.NodeType != LibraryNodeType.Playlist) return;

        var items = await Task.Run(() =>
            Orpheus.Core.Playlist.PlaylistFileReader.ReadFile(node.Path));

        if (items.Count == 0) return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);
        UpdateQueueDisplay();
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
        ActiveTab = MobileTab.Queue;
        ScheduleStateSave();
    }

    /// <summary>Append a playlist file's tracks to the end of the queue.</summary>
    public async Task EnqueuePlaylistAsync(LibraryNode node)
    {
        if (node.NodeType != LibraryNodeType.Playlist) return;

        var items = await Task.Run(() =>
            Orpheus.Core.Playlist.PlaylistFileReader.ReadFile(node.Path));

        if (items.Count == 0) return;

        _controller.Playlist.AddRange(items);
        UpdateQueueDisplay();
        ScheduleStateSave();
    }

    /// <summary>Save the current queue as a playlist file chosen by the user.</summary>
    public async Task SaveQueueAsPlaylistAsync(TopLevel topLevel)
    {
        if (_controller.Playlist.Count == 0) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title              = "Save Queue as Playlist",
            SuggestedFileName  = "Queue",
            DefaultExtension   = "m3u",
            FileTypeChoices    = new[]
            {
                new FilePickerFileType("M3U Playlist") { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("PLS Playlist") { Patterns = new[] { "*.pls" } },
            }
        });

        if (file is null) return;

        // Always write via the SAF stream — direct filesystem writes are blocked
        // by Android scoped storage on API 29+. The resolved path is only used
        // to provide relative paths in the M3U; it is not used for the write itself.
        var resolvedPath = ResolveStorageFilePath(file);
        var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
        var playlist = _controller.Playlist;

        await using (var stream = await file.OpenWriteAsync())
        {
            await Task.Run(() =>
                Orpheus.Core.Playlist.PlaylistFileWriter.WriteToStream(playlist, stream, ext));
        }

        // Rebuild the folder tree so the new playlist file appears immediately.
        // Works when the resolved path lands inside a watched folder.
        await RefreshFolderTreeAsync();
    }

    /// <summary>
    /// Rebuilds the library folder tree from disk without re-scanning the audio database
    /// or resetting navigation. Used after a playlist file is saved so it appears
    /// immediately in the library view.
    /// </summary>
    private async Task RefreshFolderTreeAsync()
    {
        var folders = _watchedFolders.ToList();
        var tracks  = _allTracks;
        var roots   = await Task.Run(() => BuildFolderTree(folders, tracks));

        _libraryRoots.Clear();
        foreach (var n in roots) _libraryRoots.Add(n);

        // If we're at the root level, refresh the displayed nodes too.
        if (_currentNode is null)
            ShowRootNodes();
    }

    /// <summary>Play all tracks in the current folder (replaces queue).</summary>
    public async Task PlayFolderAsync(LibraryNode node)
    {
        if (!Directory.Exists(node.Path)) return;

        // Filter + sort on the thread pool — iterating _allTracks (6000+ items)
        // and building PlaylistItems is measurable work that blocks the UI thread.
        var tracks = await Task.Run(() =>
            _allTracks
                .Where(t => t.FilePath.StartsWith(node.Path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.TrackNumber ?? 0)
                .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList());

        if (tracks.Count == 0) return;

        // Build PlaylistItems on thread pool — one Changed event via AddRange instead of N×Add.
        var items = await Task.Run(() => tracks.Select(TrackToPlaylistItem).ToList());
        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);

        UpdateQueueDisplay();

        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);

        ActiveTab = MobileTab.Queue;
        ScheduleStateSave();
    }

    /// <summary>Add a track to the end of the queue without interrupting playback.</summary>
    public void EnqueueTrack(TrackRow row)
    {
        AddTrackToPlaylist(row.Track);
        UpdateQueueDisplay();
        ScheduleStateSave();
    }

    /// <summary>Add all tracks in a folder to the end of the queue.</summary>
    public async Task EnqueueFolder(LibraryNode node)
    {
        if (!Directory.Exists(node.Path)) return;

        var tracks = await Task.Run(() =>
            _allTracks
                .Where(t => t.FilePath.StartsWith(node.Path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.TrackNumber ?? 0)
                .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList());

        var items = await Task.Run(() => tracks.Select(TrackToPlaylistItem).ToList());
        _controller.Playlist.AddRange(items);
        UpdateQueueDisplay();
        ScheduleStateSave();
    }

    private static PlaylistItem TrackToPlaylistItem(LibraryTrack track) =>
        new PlaylistItem
        {
            Source = MediaSource.FromFile(track.FilePath),
            Metadata = new TrackMetadata
            {
                Title    = track.Title,
                Artist   = track.Artist,
                Album    = track.Album,
                Duration = track.Duration,
            }
        };

    private void AddTrackToPlaylist(LibraryTrack track)
    {
        _controller.Playlist.Add(new PlaylistItem
        {
            Source = MediaSource.FromFile(track.FilePath),
            Metadata = new TrackMetadata
            {
                Title    = track.Title,
                Artist   = track.Artist,
                Album    = track.Album,
                Duration = track.Duration,
            }
        });
    }

    private void UpdateQueueDisplay()
    {
        // Snapshot the playlist items so background work doesn't race with
        // mutations on the UI thread.
        var mode  = _trackDisplayMode;
        var items = _controller.Playlist.ToList();

        // Build the display list on the thread pool — for large libraries this
        // loop is measurable work (string allocations, path operations, etc.).
        _ = Task.Run(() =>
        {
            var newQueue = new List<QueueItem>(items.Count);
            foreach (var item in items)
            {
                ResolveDisplayText(mode, item.Metadata,
                    item.Source.Type == Orpheus.Core.Media.MediaSourceType.LocalFile
                        ? item.Source.Uri.LocalPath : item.Source.Uri.ToString(),
                    item.DisplayName,
                    out var primary, out var secondary);
                newQueue.Add(new QueueItem(
                    primary,
                    secondary,
                    FormatTime(item.Metadata?.Duration ?? TimeSpan.Zero)));
            }
            return newQueue;
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                _queue = t.Result;
                NotifyQueueChanged();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void NotifyQueueChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Queue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueSummary)));
    }

    /// <summary>Resolves primary + secondary display text for a playlist item.</summary>
    private static void ResolveDisplayText(
        QueueDisplayMode mode,
        Orpheus.Core.Metadata.TrackMetadata? metadata,
        string filePath,
        string fallbackDisplayName,
        out string primary,
        out string secondary)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(metadata?.Title);
        var hasAlbum = !string.IsNullOrWhiteSpace(metadata?.Album);
        var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
        var folder   = System.IO.Path.GetFileName(
                           System.IO.Path.GetDirectoryName(filePath) ?? "") ?? "";

        switch (mode)
        {
            case QueueDisplayMode.FileNameFolder:
                primary   = fileName.Length > 0 ? fileName : fallbackDisplayName;
                secondary = folder;
                break;

            case QueueDisplayMode.TitleAlbumWithFallback:
                if (hasTitle)
                {
                    primary   = metadata!.Title!;
                    secondary = hasAlbum ? metadata!.Album! : folder;
                }
                else
                {
                    primary   = fileName.Length > 0 ? fileName : fallbackDisplayName;
                    secondary = folder;
                }
                break;

            default: // TitleAlbum
                primary   = hasTitle ? metadata!.Title!  : fallbackDisplayName;
                secondary = hasAlbum ? metadata!.Album! : "";
                break;
        }
    }

    // ── Controller event handlers ─────────────────────────────────────

    private void OnPlaylistIndexChanged(object? sender, int index)
    {
        CurrentQueueIndex = index;
        var item = _controller.Playlist.CurrentItem;
        if (item is null) return;

        var filePath = item.Source.Type == Orpheus.Core.Media.MediaSourceType.LocalFile
            ? item.Source.Uri.LocalPath : item.Source.Uri.ToString();
        ResolveDisplayText(_trackDisplayMode, item.Metadata, filePath, item.DisplayName,
            out var primary, out var secondary);

        NowPlayingTitle  = primary;
        NowPlayingArtist = secondary;
        NowPlayingAlbum  = item.Metadata?.Album ?? "";
    }

    private void OnControllerStateChanged(object? sender, PlaybackStateSnapshot snap)
    {
        IsPlaying        = snap.IsPlaying;
        IsActive         = !snap.IsStopped;
        PlaybackDuration = snap.Duration.TotalSeconds;
        if (!_isUserSeekingPosition)
            SetField(ref _playbackPosition, snap.Position.TotalSeconds, nameof(PlaybackPosition));
        UpdateNowPlayingTime(snap.Position, snap.Duration);
    }

    private void OnControllerPositionChanged(object? sender, PositionSnapshot snap)
    {
        PlaybackDuration = snap.Duration.TotalSeconds;
        if (!_isUserSeekingPosition)
            SetField(ref _playbackPosition, snap.Position.TotalSeconds, nameof(PlaybackPosition));
        UpdateNowPlayingTime(snap.Position, snap.Duration);
    }

    private void UpdateNowPlayingTime(TimeSpan position, TimeSpan duration)
        => NowPlayingTime = $"{FormatTime(position)} / {FormatTime(duration)}";

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");

    // ── Queue state persistence ───────────────────────────────────────

    private CancellationTokenSource? _saveCts;

    private void ScheduleStateSave()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500, token);
            if (!token.IsCancellationRequested)
                SaveState();
        }, token);
    }

    private void SaveState()
    {
        var state = new MobileState
        {
            Volume     = _volume,
            AudioDevice = null,
            QueuePaths = _controller.Playlist.Select(p =>
                p.Source.Type == Orpheus.Core.Media.MediaSourceType.LocalFile
                    ? p.Source.Uri.LocalPath
                    : "").Where(p => !string.IsNullOrEmpty(p)).ToList(),
            QueueIndex = _controller.Playlist.CurrentIndex,
        };
        state.Save();
    }

    private async Task RestoreQueueAsync(IReadOnlyList<string> paths, int index)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var track = await _library.GetTrackByPathAsync(path);
            if (track is not null)
            {
                AddTrackToPlaylist(track);
            }
            else
            {
                // File not yet in library, add minimal entry
                _controller.Playlist.Add(new PlaylistItem
                {
                    Source = MediaSource.FromFile(path),
                });
            }
        }
        UpdateQueueDisplay();
    }

    // ── Icon helpers ──────────────────────────────────────────────────

    private IImage? LoadIcon(string name, bool active = false, bool muted = false)
    {
        // Resolve color from application resources when available,
        // falling back to the default Muse gold / white.
        Color color;
        if (muted)
        {
            color = Color.Parse("#666666");
        }
        else if (active)
        {
            color = ResolveResourceColor("IconActiveColor", Colors.White);
        }
        else
        {
            color = ResolveResourceColor("IconColor", Color.Parse("#D4A843"));
        }

        var uri = $"avares://Orpheus.Android/assets/icons/{name}.svg";
        return SvgIconHelper.Load(uri, color);
    }

    private static Color ResolveResourceColor(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var raw)
            && raw is Color c)
            return c;
        return fallback;
    }

    // ── Library tree builder ──────────────────────────────────────────

    private static List<LibraryNode> BuildFolderTree(
        IReadOnlyList<string> folders,
        IReadOnlyList<LibraryTrack> tracks)
    {
        var nodes = new List<LibraryNode>();
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

    private static LibraryNode? BuildFolderNode(
        string path,
        IReadOnlyList<LibraryTrack> tracks)
    {
        var children = new List<LibraryNode>();
        if (Directory.Exists(path))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var childNode = BuildFolderNode(child, tracks);
                    if (childNode is not null)
                        children.Add(childNode);
                }

                // Playlist files — always shown, sorted by name
                foreach (var file in Directory.EnumerateFiles(path)
                             .Where(f => Orpheus.Core.Library.FolderScanner.PlaylistExtensions
                                 .Contains(Path.GetExtension(f)))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var ext = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    children.Add(new LibraryNode(
                        Path.GetFileNameWithoutExtension(file), ext, file,
                        nodeType: LibraryNodeType.Playlist));
                }
            }
            catch { /* permission denied etc. */ }
        }

        // Count tracks directly in this folder
        var directTrackCount = tracks.Count(t =>
            string.Equals(
                Path.GetDirectoryName(t.FilePath),
                path,
                StringComparison.OrdinalIgnoreCase));

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;

        var meta = directTrackCount > 0
            ? $"{directTrackCount} track{(directTrackCount == 1 ? "" : "s")}"
            : "";

        // Skip empty leaf folders that have no tracks anywhere underneath
        // (but keep folders that contain playlist files)
        var totalTracks = CountTracksUnder(path, tracks);
        var hasPlaylists = children.Any(c => c.NodeType == LibraryNodeType.Playlist);
        if (totalTracks == 0 && children.Count == 0 && !hasPlaylists)
            return null;

        return new LibraryNode(name, meta, path, children);
    }

    private static int CountTracksUnder(string path, IReadOnlyList<LibraryTrack> tracks)
        => tracks.Count(t => t.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase));

    // ── INotifyPropertyChanged helper ────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    // ── Disposal ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        SaveState();
        await _controller.DisposeAsync();
        _library.Dispose();
    }
}

// ── Supporting types ──────────────────────────────────────────────────

public enum LibraryNodeType { Folder, Playlist }

public sealed class LibraryNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string          Name     { get; }
    public string          Meta     { get; set; }
    public string          Path     { get; }
    public LibraryNodeType NodeType { get; }
    public ObservableCollection<LibraryNode> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool HasChildren  => Children.Count > 0;
    public bool IsPlaylist   => NodeType == LibraryNodeType.Playlist;
    public bool IsFolder     => NodeType == LibraryNodeType.Folder;

    public LibraryNode(string name, string meta, string path,
        IList<LibraryNode>? children = null,
        LibraryNodeType nodeType = LibraryNodeType.Folder)
    {
        Name     = name;
        Meta     = meta;
        Path     = path;
        NodeType = nodeType;
        Children = children != null
            ? new ObservableCollection<LibraryNode>(children)
            : new ObservableCollection<LibraryNode>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TrackRow
{
    public LibraryTrack Track { get; }
    public string Title    { get; }
    public string Artist   { get; }
    public string Album    { get; }
    public string Duration { get; }
    public string TrackNumber { get; }

    public TrackRow(LibraryTrack track)
    {
        Track       = track;
        Title       = track.Title ?? System.IO.Path.GetFileNameWithoutExtension(track.FilePath);
        Artist      = track.Artist ?? "";
        Album       = track.Album ?? "";
        Duration    = track.Duration.HasValue ? FormatTime(track.Duration.Value) : "";
        TrackNumber = track.TrackNumber.HasValue ? track.TrackNumber.Value.ToString() : "";
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
}

public sealed record QueueItem(string Primary, string Secondary, string Duration);

// ── Session state ─────────────────────────────────────────────────────

internal sealed class MobileState
{
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 72;

    [JsonPropertyName("audioDevice")]
    public string? AudioDevice { get; set; }

    [JsonPropertyName("queuePaths")]
    public List<string> QueuePaths { get; set; } = new();

    [JsonPropertyName("queueIndex")]
    public int QueueIndex { get; set; } = -1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MobileState Load()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path)) return new MobileState();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MobileState>(json, JsonOptions) ?? new MobileState();
        }
        catch { return new MobileState(); }
    }

    public void Save()
    {
        try
        {
            var path = GetStatePath();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    private static string GetStatePath()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(data))
            data = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return System.IO.Path.Combine(data, "OrpheusMP", "state.json");
    }
}

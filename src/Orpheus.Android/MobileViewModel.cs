using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly MetadataWorker _metadataWorker;
    private readonly PlayerController _controller;
    private readonly ILibraryChangeMonitor _changeMonitor;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _changeDebounceCts;

    // ── Snapshot-rebuild coalescing ───────────────────────────────────
    // At most one BuildFolderTree runs at a time.  If a rebuild request
    // arrives while one is already in progress, we set the dirty flag so
    // the running rebuild does one more pass when it finishes.
    private readonly SemaphoreSlim _snapshotSemaphore = new(1, 1);
    private volatile bool _snapshotDirty;
    private volatile bool _snapshotResetNavPending;

    // Folders that received change events while a scan was already running.
    // Drained and scanned after the current scan completes.
    private readonly HashSet<string> _pendingScanFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingScanLock = new();

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
    // Queue uses ObservableCollection<QueueItemViewModel> so that:
    //  • Move() fires a single CollectionChanged(Move) — ListBox containers survive
    //    the reorder and ContainerFromIndex stays valid during drag gestures.
    //  • RemoveAt/Clear fire fine-grained events without rebuilding the whole list.
    //  • IsPlaying on each item can be toggled individually to highlight the
    //    currently-playing row without replacing the entire collection.
    private readonly ObservableCollection<QueueItemViewModel> _queue = new();
    private readonly ObservableCollection<LibraryNode> _libraryRoots = new();
    // The items shown in the library tab body (either root nodes or the
    // sub-folders + tracks of the current navigation node).
    private readonly ObservableCollection<LibraryNode> _displayedNodes = new();
    // Tracks shown when a folder node is selected
    private readonly ObservableCollection<TrackRow>    _displayedTracks = new();

    // ── All library tracks cache (for folder browsing) ───────────────
    private readonly Dictionary<string, LibraryTrack> _trackIndex = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LibraryTrack> _allTracks = Array.Empty<LibraryTrack>();

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Scan progress events (forwarded for SettingsViewModel) ────────

    /// <summary>Forwarded from the internal FolderScanner so the Settings UI can show per-folder progress bars.</summary>
    public event EventHandler<LibraryScanProgress>? ScannerProgress;
    /// <summary>Forwarded from the internal MetadataWorker so the Settings UI can show per-folder metadata bars.</summary>
    public event EventHandler<LibraryScanProgress>? MetadataProgress;

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
        private set
        {
            var old = _currentQueueIndex;
            if (!SetField(ref _currentQueueIndex, value)) return;

            // Toggle IsPlaying on affected items only — avoids a full collection rebuild.
            if (old >= 0 && old < _queue.Count)
                _queue[old].IsPlaying = false;
            if (value >= 0 && value < _queue.Count)
                _queue[value].IsPlaying = true;
        }
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

    public ObservableCollection<QueueItemViewModel> Queue   => _queue;
    public ObservableCollection<LibraryNode> LibraryRoots   => _libraryRoots;
    public ObservableCollection<LibraryNode> DisplayedNodes  => _displayedNodes;
    public ObservableCollection<TrackRow>    DisplayedTracks => _displayedTracks;
    public ObservableCollection<string>      WatchedFolders  => _watchedFolders;

    public string QueueSummary =>
        _queue.Count == 0
            ? "Queue is empty"
            : $"{_queue.Count} track{(_queue.Count == 1 ? "" : "s")}";

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
        _scanner = new FolderScanner(_library);
        _scanner.Progress += OnScanProgress;
        _scanner.Progress += OnScannerProgressForward;
        _scanner.PendingTracksAdded += OnPendingTracksAdded;
        _metadataWorker = new MetadataWorker(_library, new TagLibMetadataReader());
        _metadataWorker.Progress += OnScanProgress;
        _metadataWorker.Progress += OnMetadataProgressForward;
        _changeMonitor = new AndroidMediaStoreLibraryChangeMonitor(global::Android.App.Application.Context);
        _changeMonitor.Changed += OnLibraryChanged;

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

        // Trigger the metadata worker to process any Pending rows that were left
        // behind by a previous scan that was interrupted (e.g. app killed mid-scan).
        // This is independent of the filesystem scan and requires no permissions
        // beyond reading files we already know about.
        _ = _metadataWorker.TriggerAsync();

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
            var normalizedMusicFolder = LibraryPathNormalizer.NormalizeFolderPath(musicFolder);
            await _library.AddWatchedFolderAsync(normalizedMusicFolder);

            if (!_watchedFolders.Contains(normalizedMusicFolder, StringComparer.OrdinalIgnoreCase))
                _watchedFolders.Add(normalizedMusicFolder);

            _changeMonitor.UpdateWatchedFolders(_watchedFolders);
            await ScanFoldersAsync([normalizedMusicFolder]);
            await RefreshLibraryChangeMonitorAsync();
            return;
        }

        await RefreshLibraryChangeMonitorAsync(watched);
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
        await RunScanAsync(static (scanner, cancellationToken) => scanner.ScanAsync(cancellationToken));
    }

    // Scan only the given folders (used on add/change detection, to avoid
    // re-scanning the entire library every time).
    private async Task ScanFoldersAsync(IEnumerable<string> folders)
    {
        var folderList = folders
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folderList.Count == 0)
            return;

        await RunScanAsync((scanner, cancellationToken) => scanner.ScanFoldersAsync(folderList, cancellationToken));
    }

    private async Task LoadLibraryAsync()
    {
        var tracks = await _library.GetAllTracksAsync();
        _trackIndex.Clear();
        foreach (var track in tracks)
            _trackIndex[track.FilePath] = track;

        _allTracks = _trackIndex.Values.ToList();
        var folders = (await _library.GetWatchedFoldersAsync())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await ApplyLibrarySnapshotAsync(folders, resetNavigation: true).ConfigureAwait(false);
    }


    private NavigationSnapshot? CaptureNavigationSnapshot()
    {
        if (_currentNode is null)
            return null;

        var stackPaths = _navStack
            .Reverse()
            .Select(static node => node.Path)
            .Where(static path => !string.Equals(path, RootNodePath, StringComparison.Ordinal))
            .ToList();

        return new NavigationSnapshot(stackPaths, _currentNode.Path);
    }

    private void RestoreNavigationSnapshot(NavigationSnapshot? snapshot, IReadOnlyList<LibraryNode> roots)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.CurrentPath))
        {
            _navStack.Clear();
            _currentNode = null;
            NotifyNavChanged();
            ShowRootNodes();
            return;
        }

        var currentNode = FindNodeByPath(roots, snapshot.CurrentPath);
        if (currentNode is null)
        {
            _navStack.Clear();
            _currentNode = null;
            NotifyNavChanged();
            ShowRootNodes();
            return;
        }

        _navStack.Clear();
        _navStack.Push(CreateRootNode());

        foreach (var path in snapshot.StackPaths)
        {
            var node = FindNodeByPath(roots, path);
            if (node is not null)
                _navStack.Push(node);
        }

        _currentNode = currentNode;
        NotifyNavChanged();
        ShowNodeContents(currentNode);
    }

    private void ApplyScanBatch(LibraryScanBatch batch)
    {
        // This method is always called on the UI thread (from Dispatcher.UIThread.Post).
        // Only mutate the index and _allTracks here — they are cheap, O(batch) operations.
        var batchSw = Stopwatch.StartNew();

        if (batch.UpsertedTracks.Count > 0)
        {
            foreach (var track in batch.UpsertedTracks)
                _trackIndex[track.FilePath] = track;
        }

        if (batch.RemovedPaths.Count > 0)
        {
            foreach (var path in batch.RemovedPaths)
                _trackIndex.Remove(path);
        }

        _allTracks = _trackIndex.Values.ToList();
        Console.WriteLine($"[PERF] MobileViewModel: ApplyScanBatch index update ({batch.UpsertedTracks.Count} upserted, {batch.RemovedPaths.Count} removed) took {batchSw.ElapsedMilliseconds} ms on UI thread, total tracks now {_allTracks.Count}");

        if (batch.IsDiscoveryBatch || batch.RemovedPaths.Count > 0)
        {
            // BuildFolderTree is O(folders × tracks) — coalesce concurrent calls so
            // at most one rebuild runs at a time; a pending dirty flag causes one
            // additional rebuild after the current one finishes.
            // ScheduleLibrarySnapshotAsync handles the empty-watched-folders case
            // by falling back to the DB inside RunSnapshotRebuildLoopAsync.
            _ = ScheduleLibrarySnapshotAsync(resetNavigation: false);
            return;
        }

        RefreshDisplayedTracks();
        LibrarySummary = $"{_watchedFolders.Count} folder{(_watchedFolders.Count == 1 ? "" : "s")}, {_allTracks.Count} track{(_allTracks.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Schedules a library snapshot rebuild.  If a rebuild is already running,
    /// sets a dirty flag so the running rebuild does one more pass when it
    /// finishes — guaranteeing at most one <see cref="BuildFolderTree"/> call
    /// runs at a time while still ending on the most up-to-date track list.
    /// </summary>
    private Task ScheduleLibrarySnapshotAsync(bool resetNavigation)
    {
        // Accumulate reset-nav intent across coalesced calls.
        if (resetNavigation)
            _snapshotResetNavPending = true;

        // If the semaphore is free, grab it and run the rebuild now.
        // If it's taken, just mark dirty and let the running task pick it up.
        if (_snapshotSemaphore.CurrentCount == 0)
        {
            _snapshotDirty = true;
            Console.WriteLine("[PERF] MobileViewModel: ScheduleLibrarySnapshotAsync — rebuild already running, marked dirty");
            return Task.CompletedTask;
        }

        return RunSnapshotRebuildLoopAsync();
    }

    /// <summary>
    /// Acquires <see cref="_snapshotSemaphore"/>, runs <see cref="ApplyLibrarySnapshotCoreAsync"/>,
    /// then loops if the dirty flag was set while the rebuild was in progress.
    /// </summary>
    private async Task RunSnapshotRebuildLoopAsync()
    {
        // Best-effort non-blocking acquire; if we race with another caller that
        // just grabbed it, mark dirty and exit (the other task will loop).
        if (!await _snapshotSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _snapshotDirty = true;
            Console.WriteLine("[PERF] MobileViewModel: RunSnapshotRebuildLoopAsync — lost race for semaphore, marked dirty");
            return;
        }

        try
        {
            do
            {
                _snapshotDirty = false;
                var resetNav = _snapshotResetNavPending;
                _snapshotResetNavPending = false;

                // Capture folder list on whatever thread we're on (safe because
                // _watchedFolders mutations only happen on the UI thread via
                // ApplyLibrarySnapshotCoreAsync's InvokeAsync block).
                IReadOnlyList<string> folders;
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    folders = _watchedFolders.ToList();
                }
                else
                {
                    folders = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => _watchedFolders.ToList()).GetTask().ConfigureAwait(false);
                }

                if (folders.Count == 0)
                {
                    // No watched folders yet — fall back to DB query.
                    folders = (await _library.GetWatchedFoldersAsync().ConfigureAwait(false))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                await ApplyLibrarySnapshotCoreAsync(folders, resetNav).ConfigureAwait(false);

            } while (_snapshotDirty); // re-run if another batch arrived while we were building
        }
        finally
        {
            _snapshotSemaphore.Release();
        }
    }

    /// <summary>
    /// Runs <see cref="BuildFolderTree"/> on the thread pool and marshals only
    /// the resulting node list back to the UI thread.  This prevents the O(N²)
    /// tree-build from blocking Android's UI looper on every scan batch.
    /// Called exclusively from <see cref="RunSnapshotRebuildLoopAsync"/> which
    /// guarantees single-at-a-time execution via the semaphore.
    /// </summary>
    private async Task ApplyLibrarySnapshotCoreAsync(IReadOnlyList<string> folders, bool resetNavigation)
    {
        var totalSw = Stopwatch.StartNew();
        var normalizedFolders = LibraryPathNormalizer.NormalizeDistinctFolders(folders);

        // Capture a snapshot of the navigation state and track list on the UI thread
        // before going async, so the thread-pool work reads consistent data.
        NavigationSnapshot? navigationSnapshot = null;
        IReadOnlyList<LibraryTrack> tracksSnapshot = _allTracks;

        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                navigationSnapshot = resetNavigation ? null : CaptureNavigationSnapshot();
                tracksSnapshot     = _allTracks;
            }).GetTask().ConfigureAwait(false);
        }
        else
        {
            navigationSnapshot = resetNavigation ? null : CaptureNavigationSnapshot();
            tracksSnapshot     = _allTracks;
        }

        Console.WriteLine($"[PERF] MobileViewModel: BuildFolderTree starting ({tracksSnapshot.Count} tracks, {normalizedFolders.Count} folders)");

        // Build the tree entirely off the UI thread.
        var treeSw = Stopwatch.StartNew();
        var roots = await Task.Run(() => BuildFolderTree(normalizedFolders, tracksSnapshot))
            .ConfigureAwait(false);
        Console.WriteLine($"[PERF] MobileViewModel: BuildFolderTree took {treeSw.ElapsedMilliseconds} ms, produced {roots.Count} root nodes");

        // Marshal back to the UI thread for all collection mutations.
        var uiSw = Stopwatch.StartNew();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _libraryRoots.Clear();
            foreach (var node in roots)
                _libraryRoots.Add(node);

            _watchedFolders.Clear();
            foreach (var folder in normalizedFolders)
                _watchedFolders.Add(folder);

            _changeMonitor.UpdateWatchedFolders(normalizedFolders);

            if (resetNavigation)
            {
                _navStack.Clear();
                _currentNode = null;
                NotifyNavChanged();
                ShowRootNodes();
            }
            else
            {
                RestoreNavigationSnapshot(navigationSnapshot, roots);
            }

            var trackCount  = _allTracks.Count;
            var folderCount = normalizedFolders.Count;
            LibrarySummary  = $"{folderCount} folder{(folderCount == 1 ? "" : "s")}, {trackCount} track{(trackCount == 1 ? "" : "s")}";
            Console.WriteLine($"[PERF] MobileViewModel: UI collection update took {uiSw.ElapsedMilliseconds} ms");
        });

        Console.WriteLine($"[PERF] MobileViewModel: ApplyLibrarySnapshotCoreAsync total {totalSw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Legacy entry point kept for callers that need to pass an explicit folder list
    /// (e.g. <see cref="LoadLibraryAsync"/>, <see cref="RefreshFolderTreeAsync"/>).
    /// Routes through the coalescing scheduler.
    /// </summary>
    private Task ApplyLibrarySnapshotAsync(IReadOnlyList<string> folders, bool resetNavigation)
    {
        // Pre-populate _watchedFolders with the caller-supplied list so that the
        // scheduler loop (which reads _watchedFolders) sees the right set.
        // This must happen on the UI thread; callers of this method already ensure that
        // via ConfigureAwait(false) after an await, or are on the UI thread directly.
        // We do it inline here for the non-coalesced path only.
        if (resetNavigation)
            _snapshotResetNavPending = true;

        // Run the core directly (still single-at-a-time via the semaphore loop).
        return RunSnapshotRebuildLoopWithFoldersAsync(folders, resetNavigation);
    }

    private async Task RunSnapshotRebuildLoopWithFoldersAsync(IReadOnlyList<string> folders, bool resetNavigation)
    {
        if (!await _snapshotSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _snapshotDirty = true;
            if (resetNavigation) _snapshotResetNavPending = true;
            Console.WriteLine("[PERF] MobileViewModel: RunSnapshotRebuildLoopWithFoldersAsync — lost race for semaphore, marked dirty");
            return;
        }

        try
        {
            // First pass uses the supplied folder list; subsequent dirty passes read
            // _watchedFolders (which will have been updated by the first pass's UI block).
            bool firstPass = true;
            do
            {
                _snapshotDirty = false;
                var resetNav = _snapshotResetNavPending;
                _snapshotResetNavPending = false;

                IReadOnlyList<string> passFolders;
                if (firstPass)
                {
                    passFolders = folders;
                    firstPass   = false;
                }
                else
                {
                    passFolders = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => (IReadOnlyList<string>)_watchedFolders.ToList()).GetTask().ConfigureAwait(false);

                    if (passFolders.Count == 0)
                        passFolders = (await _library.GetWatchedFoldersAsync().ConfigureAwait(false))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                }

                await ApplyLibrarySnapshotCoreAsync(passFolders, resetNav).ConfigureAwait(false);

            } while (_snapshotDirty);
        }
        finally
        {
            _snapshotSemaphore.Release();
        }
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
            _navStack.Push(CreateRootNode());

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

        if (parent.Path == RootNodePath)
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
                .Where(t => string.Equals(t.FolderPath, node.Path, StringComparison.OrdinalIgnoreCase))
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
        var normalizedPath = LibraryPathNormalizer.NormalizeFolderPath(path);
        await _library.AddWatchedFolderAsync(normalizedPath);

        if (!_watchedFolders.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            _watchedFolders.Add(normalizedPath);

        await ScanFoldersAsync([normalizedPath]);   // scan only the newly added folder
        await RefreshLibraryChangeMonitorAsync();
    }

    public async Task RemoveFolderAsync(string path)
    {
        await _library.RemoveWatchedFolderAsync(path);
        await _library.RemoveTracksUnderFolderAsync(path);
        await LoadLibraryAsync();
        await RefreshLibraryChangeMonitorAsync();
    }

    public async Task ResetLibraryAsync()
    {
        await _library.ClearAsync();
        await LoadLibraryAsync();
        _changeMonitor.UpdateWatchedFolders([]);
    }

    public async Task RescanAsync() => await ScanAsync();

    /// <summary>Rescans a single watched folder (or any sub-folder path).</summary>
    public async Task RescanFolderAsync(string path) => await ScanFoldersAsync([path]);

    /// <summary>
    /// Returns per-folder track totals and pending-metadata counts from the DB.
    /// Used by the Settings screen to seed progress bars after a past scan.
    /// </summary>
    public Task<IReadOnlyDictionary<string, (int Total, int Pending)>> GetFolderStatsAsync()
        => _library.GetFolderStatsAsync();

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

        // Capture whether the item being removed is the one currently playing,
        // and what index it occupied, before mutating anything.
        var wasPlaying = IsPlaying && index == _controller.Playlist.CurrentIndex;

        _controller.Playlist.RemoveAt(index);
        _queue.RemoveAt(index);

        if (wasPlaying)
        {
            // Play whatever is now at the same position (the next song slid into
            // the vacated slot), or the new last item if we removed the last one.
            // Playlist.RemoveAt reset _currentIndex to -1; reset _currentQueueIndex
            // to -1 here too so that the CurrentQueueIndex setter always sees a
            // genuine change and fires, toggling the IsPlaying highlight correctly.
            _currentQueueIndex = -1;
            var nextIndex = _queue.Count > 0 ? Math.Min(index, _queue.Count - 1) : -1;
            if (nextIndex >= 0)
                await _controller.PlayAtIndexAsync(nextIndex);
            else
                await _controller.StopAsync();
        }
        else
        {
            // Re-apply IsPlaying after removal (index may have shifted).
            UpdateQueuePlayingFlag();
        }

        NotifyQueueChanged();
        ScheduleStateSave();
    }

    public async Task ClearQueueAsync()
    {
        _controller.Playlist.Clear();
        _queue.Clear();
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

        // ObservableCollection.Move fires a single CollectionChanged(Move) which lets
        // the ListBox shift the container in place instead of destroying/recreating it.
        // This keeps ContainerFromIndex valid throughout an active drag gesture.
        _queue.Move(fromIndex, toIndex);

        // After move the currently-playing index inside the playlist may have changed;
        // re-sync the IsPlaying flags to the new controller index.
        UpdateQueuePlayingFlag();
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
        await ApplyLibrarySnapshotAsync(folders, resetNavigation: false).ConfigureAwait(false);
    }

    private async Task RefreshLibrarySnapshotAsync(bool resetNavigation)
    {
        var folders = (await _library.GetWatchedFoldersAsync().ConfigureAwait(false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await ApplyLibrarySnapshotAsync(folders, resetNavigation).ConfigureAwait(false);
    }

    /// <summary>Play all tracks in the current folder (replaces queue).</summary>
    public async Task PlayFolderAsync(LibraryNode node)
    {
        if (!Directory.Exists(node.Path)) return;

        // Filter + sort on the thread pool — iterating _allTracks (6000+ items)
        // and building PlaylistItems is measurable work that blocks the UI thread.
        var tracks = await Task.Run(() =>
            _allTracks
                .Where(t => IsTrackUnderFolder(t, node.Path))
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
                .Where(t => IsTrackUnderFolder(t, node.Path))
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
        // Snapshot the playlist so background work doesn't race with UI mutations.
        var mode        = _trackDisplayMode;
        var items       = _controller.Playlist.ToList();
        var playingIdx  = _controller.Playlist.CurrentIndex;

        // Build view-model list on the thread pool — string allocations for large
        // queues are measurable and must not block the UI thread.
        _ = Task.Run(() =>
        {
            var vms = new List<QueueItemViewModel>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                ResolveDisplayText(mode, item.Metadata,
                    item.Source.Type == Orpheus.Core.Media.MediaSourceType.LocalFile
                        ? item.Source.Uri.LocalPath : item.Source.Uri.ToString(),
                    item.DisplayName,
                    out var primary, out var secondary);
                vms.Add(new QueueItemViewModel(
                    primary,
                    secondary,
                    FormatTime(item.Metadata?.Duration ?? TimeSpan.Zero),
                    isPlaying: i == playingIdx));
            }
            return vms;
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;

            // Replace the ObservableCollection contents in one pass on the UI thread.
            // Using Clear + bulk Add is slightly simpler than diffing; for a queue
            // rebuild (not a per-item move) this is called infrequently.
            _queue.Clear();
            foreach (var vm in t.Result)
                _queue.Add(vm);

            // playingIdx was captured before PlayAtIndexAsync() was called, so it
            // may be stale (e.g. -1 when play started while the build was running).
            // Re-sync IsPlaying flags now that the collection is fully populated.
            UpdateQueuePlayingFlag();

            NotifyQueueChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Syncs <see cref="QueueItemViewModel.IsPlaying"/> flags to the current playlist
    /// index without touching the rest of the collection.  Called after moves/removes
    /// that may have shifted the playing index.
    /// </summary>
    private void UpdateQueuePlayingFlag()
    {
        var idx = _controller.Playlist.CurrentIndex;
        for (var i = 0; i < _queue.Count; i++)
            _queue[i].IsPlaying = i == idx;
    }

    private void NotifyQueueChanged()
    {
        // Queue is an ObservableCollection — it raises its own CollectionChanged.
        // We only need to explicitly notify the derived scalar properties.
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
        IsPlaying = snap.IsPlaying;
        IsActive  = !snap.IsStopped;
        if (snap.IsStopped)
        {
            // Reset position before zeroing duration so Value never exceeds
            // Maximum during the two-step update (avoids 0/0 = NaN fill).
            if (!_isUserSeekingPosition)
                SetField(ref _playbackPosition, 0d, nameof(PlaybackPosition));
            PlaybackDuration = 0;
            UpdateNowPlayingTime(TimeSpan.Zero, TimeSpan.Zero);
        }
        else
        {
            // Reset to 0 rather than using snap.Position: VLC may not have
            // updated the clock for the new track yet, so snap.Position can
            // carry the previous track's end time. The PositionChanged timer
            // will provide accurate values within 100 ms.
            if (!_isUserSeekingPosition)
                SetField(ref _playbackPosition, 0d, nameof(PlaybackPosition));
            PlaybackDuration = snap.Duration.TotalSeconds;
            UpdateNowPlayingTime(TimeSpan.Zero, snap.Duration);
        }
    }

    private void OnControllerPositionChanged(object? sender, PositionSnapshot snap)
    {
        // Ignore stale ticks that arrive after playback has stopped.
        if (!_isActive) return;

        if (!_isUserSeekingPosition)
        {
            // Update duration before position so Maximum >= Value and a bound
            // Slider never clamps to 100% for a frame.
            PlaybackDuration = snap.Duration.TotalSeconds;
            SetField(ref _playbackPosition, snap.Position.TotalSeconds, nameof(PlaybackPosition));
        }
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
    //
    // O(tracks) approach:
    //   1. Pre-group tracks by their exact FolderPath into a dictionary — O(N) one-time cost.
    //   2. BuildFolderNode recurses the directory tree bottom-up, using O(1) dict lookups
    //      for direct-track counts.  Each call returns the total track count under that
    //      node so the parent can accumulate the subtree total without any extra passes.
    //   3. The subtree total is a natural by-product of the bottom-up recursion —
    //      no second pass over the track list is needed.

    private static List<LibraryNode> BuildFolderTree(
        IReadOnlyList<string> folders,
        IReadOnlyList<LibraryTrack> tracks)
    {
        // Build the lookup once for the whole tree — O(N).
        var byFolder = new Dictionary<string, List<LibraryTrack>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var fp = track.FolderPath ?? Path.GetDirectoryName(track.FilePath) ?? "";
            if (!byFolder.TryGetValue(fp, out var list))
                byFolder[fp] = list = new List<LibraryTrack>();
            list.Add(track);
        }

        var nodes = new List<LibraryNode>();
        foreach (var folder in folders)
        {
            var (node, _) = BuildFolderNode(folder, byFolder, isRoot: true);
            if (node is not null)
            {
                node.IsExpanded = true;
                nodes.Add(node);
            }
        }
        return nodes;
    }

    /// <summary>
    /// Recursively builds a <see cref="LibraryNode"/> for <paramref name="path"/>.
    /// Returns the node (or <c>null</c> if the folder should be pruned) and the
    /// total number of tracks anywhere under this folder (used by the parent to
    /// accumulate subtree counts without extra passes).
    /// </summary>
    private static (LibraryNode? node, int totalTracks) BuildFolderNode(
        string path,
        Dictionary<string, List<LibraryTrack>> byFolder,
        bool isRoot = false)
    {
        var children = new List<LibraryNode>();
        int childTrackTotal = 0;

        if (Directory.Exists(path))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var (childNode, childTracks) = BuildFolderNode(child, byFolder);
                    if (childNode is not null)
                    {
                        children.Add(childNode);
                        childTrackTotal += childTracks;
                    }
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

        // O(1) direct-track count via pre-grouped dictionary.
        var directTrackCount = byFolder.TryGetValue(path, out var direct) ? direct.Count : 0;
        var totalTracks      = directTrackCount + childTrackTotal;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;

        // Count playlist files anywhere in this subtree
        var playlistCount = children.Sum(c => c.NodeType == LibraryNodeType.Playlist ? 1 : CountPlaylists(c));

        // Build meta: total tracks in subtree + playlist count, for all folder nodes.
        // Root nodes also prepend the full path so two libraries with the same name are distinguishable.
        var metaParts = new System.Text.StringBuilder();
        if (isRoot)
            metaParts.Append(path);
        if (totalTracks > 0)
        {
            if (metaParts.Length > 0) metaParts.Append("  ·  ");
            metaParts.Append($"{totalTracks} track{(totalTracks == 1 ? "" : "s")}");
        }
        if (playlistCount > 0)
        {
            if (metaParts.Length > 0) metaParts.Append("  ·  ");
            metaParts.Append($"{playlistCount} playlist{(playlistCount == 1 ? "" : "s")}");
        }
        var meta = metaParts.ToString();

        // Prune empty leaf folders (no tracks anywhere underneath, no playlist files).
        // Always keep the root watched folder even if it happens to be empty.
        var hasPlaylists = children.Any(c => c.NodeType == LibraryNodeType.Playlist);
        if (!isRoot && totalTracks == 0 && children.Count == 0 && !hasPlaylists)
            return (null, 0);

        return (new LibraryNode(name, meta, path, children), totalTracks);
    }

    private static int CountPlaylists(LibraryNode node)
    {
        var count = 0;
        foreach (var child in node.Children)
            count += child.NodeType == LibraryNodeType.Playlist ? 1 : CountPlaylists(child);
        return count;
    }

    private void RefreshDisplayedTracks()
    {
        if (_currentNode is null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryEmpty)));
            return;
        }

        ShowNodeContents(_currentNode);
    }

    private static bool IsTrackUnderFolder(LibraryTrack track, string folderPath)
        => LibraryPathNormalizer.IsPathWithinFolder(track.FilePath, folderPath);

    private static LibraryNode? FindNodeByPath(IEnumerable<LibraryNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
                return node;

            var child = FindNodeByPath(node.Children, path);
            if (child is not null)
                return child;
        }

        return null;
    }

    private static LibraryNode CreateRootNode() => new(RootNodePath, "", RootNodePath);

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
        _changeDebounceCts?.Cancel();
        _changeDebounceCts?.Dispose();
        _changeMonitor.Changed -= OnLibraryChanged;
        _scanner.Progress -= OnScanProgress;
        _scanner.Progress -= OnScannerProgressForward;
        _scanner.PendingTracksAdded -= OnPendingTracksAdded;
        _metadataWorker.Progress -= OnScanProgress;
        _metadataWorker.Progress -= OnMetadataProgressForward;
        await _changeMonitor.DisposeAsync();
        _scanGate.Dispose();
        _snapshotSemaphore.Dispose();
        await _controller.DisposeAsync();
        _library.Dispose();
    }

    private async Task RunScanAsync(Func<FolderScanner, CancellationToken, Task> scanAction)
    {
        await _scanGate.WaitAsync();
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning…";
            await scanAction(_scanner, CancellationToken.None);
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }

        // Drain any folder scan requests that arrived while this scan was running.
        List<string> pending;
        lock (_pendingScanLock)
        {
            pending = [.. _pendingScanFolders];
            _pendingScanFolders.Clear();
        }

        if (pending.Count > 0)
            await ScanFoldersAsync(pending);
    }

    private void OnPendingTracksAdded(object? sender, EventArgs e)
    {
        // The filesystem scanner wrote new Pending stubs — wake the metadata worker.
        _ = _metadataWorker.TriggerAsync();
    }

    private async void OnLibraryChanged(object? sender, LibraryChangeDetectedEventArgs e)
    {
        // Debounce: cancel any pending delayed trigger and restart the timer.
        var previous = Interlocked.Exchange(ref _changeDebounceCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var cts = _changeDebounceCts;
        if (cts is null)
            return;

        try
        {
            await Task.Delay(1000, cts.Token);

            if (e.RequiresFullRescan || e.FolderPaths.Count == 0)
            {
                if (IsScanning)
                {
                    // Queue all watched folders for a follow-up scan.
                    var watched = await _library.GetWatchedFoldersAsync(cts.Token);
                    lock (_pendingScanLock)
                    {
                        foreach (var f in watched)
                            _pendingScanFolders.Add(f);
                    }
                }
                else
                {
                    await ScanAsync();
                }
            }
            else
            {
                var toScanNow = new List<string>();
                lock (_pendingScanLock)
                {
                    foreach (var folder in e.FolderPaths)
                    {
                        if (IsScanning)
                            // Coalesce into the pending set; drained after current scan.
                            _pendingScanFolders.Add(folder);
                        else
                            toScanNow.Add(folder);
                    }
                }

                if (toScanNow.Count > 0)
                    await ScanFoldersAsync(toScanNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshLibraryChangeMonitorAsync(IReadOnlyList<string>? watchedFolders = null)
    {
        watchedFolders ??= await _library.GetWatchedFoldersAsync();
        _changeMonitor.UpdateWatchedFolders(watchedFolders);
    }

    private void OnScanProgress(object? sender, LibraryScanProgress e)
    {
        var dispatchSw = Stopwatch.StartNew();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"[PERF] MobileViewModel: OnScanProgress UI dispatch lag {dispatchSw.ElapsedMilliseconds} ms (IsComplete={e.IsComplete}, batch={e.Batch.UpsertedTracks.Count} upserted)");

            StatusMessage = e.IsComplete
                ? ""
                : $"Scanning... {Math.Min(e.ProcessedFiles, e.TotalFiles)}/{Math.Max(e.TotalFiles, e.ProcessedFiles)}";

            if (e.Batch.HasChanges)
                ApplyScanBatch(e.Batch);
        });
    }

    private void OnScannerProgressForward(object? sender, LibraryScanProgress e)
        => ScannerProgress?.Invoke(this, e);

    private void OnMetadataProgressForward(object? sender, LibraryScanProgress e)
        => MetadataProgress?.Invoke(this, e);

    private const string RootNodePath = "__root__";

    private sealed record NavigationSnapshot(IReadOnlyList<string> StackPaths, string CurrentPath);
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

/// <summary>
/// Per-row view model for the queue list.
/// Uses <see cref="INotifyPropertyChanged"/> so <see cref="IsPlaying"/> can be toggled
/// on individual items without rebuilding the entire collection.
/// </summary>
public sealed class QueueItemViewModel : INotifyPropertyChanged
{
    private bool _isPlaying;

    public string Primary   { get; }
    public string Secondary { get; }
    public string Duration  { get; }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        }
    }

    public QueueItemViewModel(string primary, string secondary, string duration, bool isPlaying = false)
    {
        Primary   = primary;
        Secondary = secondary;
        Duration  = duration;
        _isPlaying = isPlaying;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.Media;
using Orpheus.Core.Library;
using Orpheus.Core.Metadata;
using Orpheus.Core.Playback;
using Orpheus.Core.Media;
using Orpheus.Core.Playlist;
using Orpheus.Core.Effects;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using Orpheus.Desktop.Lang;
using Orpheus.Desktop.Theming;


namespace Orpheus.Desktop;

public partial class MainWindow : Window
{
    private GlobalMediaKeyService? _mediaKeyService;
#if LINUX
    private MprisService? _mprisService;
#endif

    /// <summary>
    /// The global media key service, exposed so that the settings UI can
    /// enter rebind-listening mode and update bindings at runtime.
    /// </summary>
    public GlobalMediaKeyService? MediaKeyService => _mediaKeyService;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        InitializeGlobalMediaKeys();
#if LINUX
        InitializeMpris();
#endif
    }

    private void InitializeGlobalMediaKeys()
    {
        var vm = DataContext as MainWindowViewModel;
        if (vm is null)
            return;

        var config = ((App)Avalonia.Application.Current!).Config;

        _mediaKeyService = new GlobalMediaKeyService();
        _mediaKeyService.Configure(
            config.KeyPlayPause,
            config.KeyNextTrack,
            config.KeyPreviousTrack,
            config.KeyStop,
            config.KeyVolumeUp,
            config.KeyVolumeDown);

        _mediaKeyService.ActionPressed += action =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                switch (action)
                {
                    case MediaAction.PlayPause:
                        await vm.TogglePlayPauseAsync();
                        break;
                    case MediaAction.NextTrack:
                        await vm.PlayNextAsync();
                        break;
                    case MediaAction.PreviousTrack:
                        await vm.PlayPreviousAsync();
                        break;
                    case MediaAction.Stop:
                        await vm.StopAsync();
                        break;
                    case MediaAction.VolumeUp:
                        vm.Volume = Math.Min(100, vm.Volume + 5);
                        break;
                    case MediaAction.VolumeDown:
                        vm.Volume = Math.Max(0, vm.Volume - 5);
                        break;
                }
            });
        };

        _ = _mediaKeyService.StartAsync();
    }

#if LINUX
    private void InitializeMpris()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var vm = DataContext as MainWindowViewModel;
        if (vm is null)
            return;

        _mprisService = new MprisService();

        // Wire callbacks: MPRIS D-Bus commands → ViewModel methods.
        // The Tmds.DBus handler runs on a thread-pool thread, so dispatch
        // to the UI thread where the ViewModel methods are safe to call.
        _mprisService.Configure(
            playPause: () =>
            {
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try   { await vm.TogglePlayPauseAsync(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            next: () =>
            {
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try   { await vm.PlayNextAsync(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            previous: () =>
            {
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try   { await vm.PlayPreviousAsync(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            stop: () =>
            {
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try   { await vm.StopAsync(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            play: () =>
            {
                // Play = if not active, start playback; otherwise resume
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (!vm.IsPlaying)
                            await vm.TogglePlayPauseAsync();
                        tcs.SetResult();
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            pause: () =>
            {
                var tcs = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (vm.IsPlaying)
                            await vm.TogglePlayPauseAsync();
                        tcs.SetResult();
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            seekTo: posSeconds =>
                Dispatcher.UIThread.Post(() =>
                {
                    vm.PlaybackPosition = posSeconds;
                }),
            setVolume: vol =>
                Dispatcher.UIThread.Post(() =>
                {
                    vm.Volume = vol;
                }),
            setShuffle: enabled =>
                Dispatcher.UIThread.Post(() =>
                {
                    vm.SetShuffle(enabled);
                }),
            setLoopStatus: loopStatus =>
                Dispatcher.UIThread.Post(() =>
                {
                    vm.SetLoopStatus(loopStatus);
                }));

        // Push the initial state and keep MPRIS in sync via PropertyChanged.
        _mprisService.UpdateState(vm.BuildMprisState());
        vm.PropertyChanged += OnViewModelPropertyChangedForMpris;

        _ = _mprisService.StartAsync();
    }

    /// <summary>
    /// Called on the UI thread whenever a ViewModel property changes.
    /// Pushes an updated <see cref="MprisPlayerState"/> to the D-Bus service.
    /// </summary>
#pragma warning disable CA1416 // All callers of this method are inside #if LINUX
    private void OnViewModelPropertyChangedForMpris(object? sender, PropertyChangedEventArgs e)
    {
        if (_mprisService is null) return;
        var vm = sender as MainWindowViewModel;
        if (vm is null) return;

        // Only re-push for properties that MPRIS cares about.
        // PlaybackPosition is included so MprisService can throttle-emit
        // Position at 1 Hz while playing (KDE uses it to drive the progress bar).
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsPlaying):
            case nameof(MainWindowViewModel.IsActive):
            case nameof(MainWindowViewModel.IsShuffleEnabled):
            case nameof(MainWindowViewModel.RepeatMode):
            case nameof(MainWindowViewModel.Volume):
            case nameof(MainWindowViewModel.PlaybackPosition):
            case nameof(MainWindowViewModel.PlaybackDuration):
            case nameof(MainWindowViewModel.NowPlayingTitle):
            case nameof(MainWindowViewModel.NowPlayingArtist):
            case nameof(MainWindowViewModel.NowPlayingAlbum):
            case nameof(MainWindowViewModel.NowPlayingPrimary):
            case nameof(MainWindowViewModel.NowPlayingSecondary):
                _mprisService.UpdateState(vm.BuildMprisState());
                break;
        }
    }
#pragma warning restore CA1416
#endif

    protected override void OnClosed(EventArgs e)
    {
        _mediaKeyService?.Dispose();
        _mediaKeyService = null;
#if LINUX
        if (DataContext is MainWindowViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChangedForMpris;
#pragma warning disable CA1416
        _mprisService?.Dispose();
#pragma warning restore CA1416
        _mprisService = null;
#endif
        base.OnClosed(e);
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMediaLibrary _library;
    private readonly FolderScanner _scanner;
    private readonly PlayerController _controller;
    private VlcEqualizer? _equalizer;
    private readonly ObservableCollection<LibraryNode> _libraryRoots = new();
    private readonly ObservableCollection<TrackRow> _tracks = new();
    private readonly ObservableCollection<QueueItem> _queue = new();
    private IReadOnlyList<LibraryTrack> _allTracks = Array.Empty<LibraryTrack>();
    private IReadOnlyList<TrackRow> _currentTracks = Array.Empty<TrackRow>();
    private IReadOnlyList<TrackRow> _viewTracks = Array.Empty<TrackRow>();
    private readonly string _databasePath;
    private readonly string _musicFolder;
    private bool _suppressPlaylistChanged;
    private DispatcherTimer? _expandedPathsSaveTimer;
    private DispatcherTimer? _queueStateSaveTimer;

    private string _librarySummary = Resources.LibraryLoading;
    private string _queueSummary = Resources.QueueEmpty;
    private string _nowPlayingTitle = Resources.NothingPlaying;
    private string _nowPlayingArtist = "";
    private string _nowPlayingAlbum = "";
    private string _nowPlayingPrimary = Resources.NothingPlaying;
    private string _nowPlayingSecondary = "";
    private string _nowPlayingTime = "00:00 / 00:00";
    private double _playbackDuration;
    private double _playbackPosition;
    private bool _isPositionUpdateFromPlayer;
    private bool _isUserSeekingPosition;
    private long _seekSuppressUntilTicks;
    private double _volume = 72;
    private string _searchQuery = string.Empty;
    private bool _isSearchActive;
    private int _selectedTrackIndex = -1;
    private IReadOnlyList<int> _selectedTrackIndices = Array.Empty<int>();
    private int _currentQueueIndex = -1;
    private TrackSortField _sortField = TrackSortField.Title;
    private bool _sortAscending = true;
    private bool _hideMissingTitle;
    private bool _hideMissingArtist;
    private bool _hideMissingAlbum;
    private bool _hideMissingGenre;
    private bool _hideMissingTrackNumber;
    private bool _isPlaying;
    private bool _isActive;
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
    private QueueDisplayMode _queueDisplayMode = QueueDisplayMode.Title;
    private bool _showQueueSecondaryText = true;
    private bool _showLibraryFiles;
    private bool _isQueueDirty;
    private SessionMode _sessionMode = SessionMode.Library;
    private bool _isTrackSortEnabled = true;
    private bool _isPlaylistView;
    private bool _isTrackOrderDirty;
    private string? _currentPlaylistPath;

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

    /// <summary>
    /// Primary text displayed above the seek bar, formatted according
    /// to the current QueueDisplayMode setting.
    /// </summary>
    public string NowPlayingPrimary
    {
        get => _nowPlayingPrimary;
        private set => SetField(ref _nowPlayingPrimary, value);
    }

    /// <summary>
    /// Secondary text displayed above the seek bar (e.g. artist or folder),
    /// formatted according to the current QueueDisplayMode and ShowQueueSecondaryText settings.
    /// </summary>
    public string NowPlayingSecondary
    {
        get => _nowPlayingSecondary;
        private set => SetField(ref _nowPlayingSecondary, value);
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

    /// <summary>
    /// Set to true while the user is dragging the position slider.
    /// Suppresses player-driven position updates so the slider doesn't fight the user.
    /// </summary>
    public bool IsUserSeekingPosition
    {
        get => _isUserSeekingPosition;
        set
        {
            if (!SetField(ref _isUserSeekingPosition, value))
                return;

            // When the user releases the slider, apply the seek
            if (!value)
            {
                ApplySeek(_playbackPosition);
            }
        }
    }

    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (!SetField(ref _playbackPosition, value))
                return;

            // Seek immediately if user changes position but is NOT dragging
            if (!_isPositionUpdateFromPlayer && !_isUserSeekingPosition)
            {
                ApplySeek(value);
            }
        }
    }

    /// <summary>
    /// Suppress player-driven position updates for a short window after seeking,
    /// so stale VLC callbacks don't snap the slider back to the pre-seek position.
    /// </summary>
    private const long SeekSuppressionMs = 500;

    private void ApplySeek(double positionSeconds)
    {
        _seekSuppressUntilTicks = Environment.TickCount64 + SeekSuppressionMs;
        _ = _controller.SeekAsync(TimeSpan.FromSeconds(positionSeconds));
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var oldVolume = _volume;
            if (SetField(ref _volume, value))
            {
                _controller.SetVolume(value);
                SaveConfig();

                // Re-tint the volume icon when crossing the low/high threshold
                if ((oldVolume < 35) != (value < 35))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
            }
        }
    }

    private bool _isMuted;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
        }
    }

    public void ToggleMute()
    {
        _controller.ToggleMute();
    }

    /// <summary>Volume icon — switches between low/high at 35%, dims when muted.</summary>
    public IImage? VolumeIcon =>
        SvgIconHelper.Load(
            _volume >= 35
                ? "avares://Orpheus.Desktop/assets/icons/volume-high.svg"
                : "avares://Orpheus.Desktop/assets/icons/volume-low.svg",
            _isMuted ? Color.Parse("#666666") : ResolveIconColor());

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                // Only trigger library search when the query doesn't look like a URL.
                if (!IsRadioUrl(value))
                    _ = LoadLibraryAsync(_searchQuery);
            }
        }
    }

    /// <summary>
    /// True while a search query is active. Sort and filter controls are
    /// disabled during search — results are ordered by relevance instead.
    /// </summary>
    public bool IsSearchActive
    {
        get => _isSearchActive;
        private set => SetField(ref _isSearchActive, value);
    }

    public SessionMode SessionMode
    {
        get => _sessionMode;
        private set
        {
            if (SetField(ref _sessionMode, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionModeLabel)));
        }
    }

    public string SessionModeLabel => _sessionMode switch
    {
        SessionMode.Radio  => Resources.SessionRadio,
        SessionMode.Stream => Resources.SessionStream,
        _                  => Resources.OfflineLibrary,
    };

    public int SelectedTrackIndex
    {
        get => _selectedTrackIndex;
        set
        {
            if (SetField(ref _selectedTrackIndex, value) && value >= 0)
                _selectedTrackIndices = new[] { value };
        }
    }

    public int CurrentQueueIndex
    {
        get => _currentQueueIndex;
        private set => SetField(ref _currentQueueIndex, value);
    }

    /// <summary>
    /// True when the play queue has been modified by the user (add, remove, reorder)
    /// since it was last loaded or saved.
    /// </summary>
    public bool IsQueueDirty
    {
        get => _isQueueDirty;
        private set => SetField(ref _isQueueDirty, value);
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

    public bool IsTrackSortEnabled
    {
        get => _isTrackSortEnabled;
        private set
        {
            if (SetField(ref _isTrackSortEnabled, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveTrackOrder)));
        }
    }

    public bool IsPlaylistView
    {
        get => _isPlaylistView;
        private set
        {
            if (SetField(ref _isPlaylistView, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveTrackOrder)));
        }
    }

    public bool IsTrackOrderDirty
    {
        get => _isTrackOrderDirty;
        private set
        {
            if (SetField(ref _isTrackOrderDirty, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveTrackOrder)));
        }
    }

    public bool CanSaveTrackOrder =>
        IsPlaylistView && IsTrackOrderDirty && !IsTrackSortEnabled && !string.IsNullOrWhiteSpace(_currentPlaylistPath);

    public IReadOnlyList<int> SelectedTrackIndices => _selectedTrackIndices;

    public bool HideMissingTitle
    {
        get => _hideMissingTitle;
        set
        {
            if (SetField(ref _hideMissingTitle, value))
            {
                SaveConfig();
                _ = RefreshTrackViewAsync();
            }
        }
    }

    public bool HideMissingArtist
    {
        get => _hideMissingArtist;
        set
        {
            if (SetField(ref _hideMissingArtist, value))
            {
                SaveConfig();
                _ = RefreshTrackViewAsync();
            }
        }
    }

    public bool HideMissingAlbum
    {
        get => _hideMissingAlbum;
        set
        {
            if (SetField(ref _hideMissingAlbum, value))
            {
                SaveConfig();
                _ = RefreshTrackViewAsync();
            }
        }
    }

    public bool HideMissingGenre
    {
        get => _hideMissingGenre;
        set
        {
            if (SetField(ref _hideMissingGenre, value))
            {
                SaveConfig();
                _ = RefreshTrackViewAsync();
            }
        }
    }

    public bool HideMissingTrackNumber
    {
        get => _hideMissingTrackNumber;
        set
        {
            if (SetField(ref _hideMissingTrackNumber, value))
            {
                SaveConfig();
                _ = RefreshTrackViewAsync();
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIconPrimary)));
            }
        }
    }

    /// <summary>
    /// True when playback is playing or paused (i.e. not stopped).
    /// Used to keep the play/pause button highlighted while paused.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetField(ref _isActive, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIconPrimary)));
        }
    }

    /// <summary>
    /// Play/Pause icon — uses active (white) color when active (accent background),
    /// default icon color when stopped (normal background).
    /// </summary>
    public IImage? PlayPauseIconPrimary =>
        SvgIconHelper.Load(
            _isPlaying
                ? "avares://Orpheus.Desktop/assets/icons/pause.svg"
                : "avares://Orpheus.Desktop/assets/icons/play.svg",
            _isActive ? ResolveIconActiveColor() : ResolveIconColor());

    /// <summary>Play/Pause icon in default icon color (for non-primary contexts).</summary>
    public IImage? PlayPauseIcon =>
        SvgIconHelper.Load(
            _isPlaying
                ? "avares://Orpheus.Desktop/assets/icons/pause.svg"
                : "avares://Orpheus.Desktop/assets/icons/play.svg",
            ResolveIconColor());

    /// <summary>Previous icon — default icon color (accent).</summary>
    public IImage? PreviousIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/previous.svg", ResolveIconColor());

    /// <summary>Next icon — default icon color (accent).</summary>
    public IImage? NextIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/next.svg", ResolveIconColor());

    /// <summary>Stop icon — default icon color (accent).</summary>
    public IImage? StopIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/stop.svg", ResolveIconColor());

    /// <summary>
    /// Shuffle icon — accent when off, white when on.
    /// </summary>
    public IImage? ShuffleIcon =>
        SvgIconHelper.Load(
            "avares://Orpheus.Desktop/assets/icons/shuffle.svg",
            _isShuffleEnabled ? ResolveIconActiveColor() : ResolveIconColor());

    public bool IsShuffleEnabled
    {
        get => _isShuffleEnabled;
        private set
        {
            if (SetField(ref _isShuffleEnabled, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShuffleIcon)));
        }
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatIcon)));
            }
        }
    }

    public string AppTitle => Resources.AppTitle;

    public bool IsRepeatEnabled => _repeatMode != RepeatMode.Off;

    public string RepeatLabel => _repeatMode switch
    {
        RepeatMode.All => Resources.RepeatAll,
        RepeatMode.One => Resources.RepeatOne,
        _ => Resources.Repeat
    };

    /// <summary>
    /// Repeat icon — accent when off, white when on (any repeat mode).
    /// </summary>
    public IImage? RepeatIcon
    {
        get
        {
            var path = _repeatMode switch
            {
                RepeatMode.All => "avares://Orpheus.Desktop/assets/icons/repeat-all.svg",
                RepeatMode.One => "avares://Orpheus.Desktop/assets/icons/repeat-one.svg",
                _ => "avares://Orpheus.Desktop/assets/icons/repeat-none.svg",
            };
            return SvgIconHelper.Load(path, IsRepeatEnabled ? ResolveIconActiveColor() : ResolveIconColor());
        }
    }

    // ── Static UI icons (accent-colored) ──────────────────────

    /// <summary>Search icon — default icon color (accent).</summary>
    public IImage? SearchIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/search.svg", ResolveIconColor());

    /// <summary>Equalizer icon — default icon color (accent).</summary>
    public IImage? EqualizerIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/equalizer.svg", ResolveIconColor());

    /// <summary>Settings icon — default icon color (accent).</summary>
    public IImage? SettingsIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/settings.svg", ResolveIconColor());

    /// <summary>Sort icon — default icon color (accent).</summary>
    public IImage? SortIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/sort.svg", ResolveIconColor());

    /// <summary>Filter icon — default icon color (accent).</summary>
    public IImage? FilterIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/filter.svg", ResolveIconColor());

    /// <summary>Plus icon — default icon color (accent).</summary>
    public IImage? PlusIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/plus.svg", ResolveIconColor());

    /// <summary>Ellipsis icon — default icon color (accent).</summary>
    public IImage? EllipsisIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/ellipsis.svg", ResolveIconColor());

    // ── Theme icon color helpers ─────────────────────────────

    private Color ResolveIconColor()
    {
        if (Application.Current!.Resources.TryGetResource(
                "IconColor", Application.Current.ActualThemeVariant, out var obj)
            && obj is Color color)
            return color;
        return Color.Parse("#D4A843"); // fallback
    }

    private Color ResolveIconActiveColor()
    {
        if (Application.Current!.Resources.TryGetResource(
                "IconActiveColor", Application.Current.ActualThemeVariant, out var obj)
            && obj is Color color)
            return color;
        return Colors.White; // fallback
    }

    public bool ShowTitle
    {
        get => _showTitle;
        set { if (SetField(ref _showTitle, value)) SaveConfig(); }
    }

    public bool ShowArtist
    {
        get => _showArtist;
        set { if (SetField(ref _showArtist, value)) SaveConfig(); }
    }

    public bool ShowAlbum
    {
        get => _showAlbum;
        set { if (SetField(ref _showAlbum, value)) SaveConfig(); }
    }

    public bool ShowFileName
    {
        get => _showFileName;
        set { if (SetField(ref _showFileName, value)) SaveConfig(); }
    }

    public bool ShowLength
    {
        get => _showLength;
        set { if (SetField(ref _showLength, value)) SaveConfig(); }
    }

    public bool ShowFormat
    {
        get => _showFormat;
        set { if (SetField(ref _showFormat, value)) SaveConfig(); }
    }

    public bool ShowTrackNumber
    {
        get => _showTrackNumber;
        set { if (SetField(ref _showTrackNumber, value)) SaveConfig(); }
    }

    public bool ShowDiscNumber
    {
        get => _showDiscNumber;
        set { if (SetField(ref _showDiscNumber, value)) SaveConfig(); }
    }

    public bool ShowYear
    {
        get => _showYear;
        set { if (SetField(ref _showYear, value)) SaveConfig(); }
    }

    public bool ShowGenre
    {
        get => _showGenre;
        set { if (SetField(ref _showGenre, value)) SaveConfig(); }
    }

    public bool ShowBitrate
    {
        get => _showBitrate;
        set { if (SetField(ref _showBitrate, value)) SaveConfig(); }
    }

    public QueueDisplayMode QueueDisplayMode
    {
        get => _queueDisplayMode;
        set
        {
            if (SetField(ref _queueDisplayMode, value))
            {
                Dispatcher.UIThread.Post(UpdateQueueFromPlaylist);
                UpdateNowPlayingDisplay();
                SaveConfig();
            }
        }
    }

    public bool ShowQueueSecondaryText
    {
        get => _showQueueSecondaryText;
        set
        {
            if (SetField(ref _showQueueSecondaryText, value))
            {
                Dispatcher.UIThread.Post(UpdateQueueFromPlaylist);
                UpdateNowPlayingDisplay();
                SaveConfig();
            }
        }
    }

    public bool ShowLibraryFiles
    {
        get => _showLibraryFiles;
        set
        {
            if (SetField(ref _showLibraryFiles, value))
            {
                SaveConfig();
                _ = RefreshLibraryAsync();
            }
        }
    }

    public ObservableCollection<LibraryNode> LibraryRoots => _libraryRoots;
    public ObservableCollection<TrackRow> Tracks => _tracks;
    public ObservableCollection<QueueItem> Queue => _queue;

    public void SetSelectedTrackIndices(IReadOnlyList<int> indices)
    {
        _selectedTrackIndices = indices
            .Where(index => index >= 0 && index < _tracks.Count)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        var selectedIndex = _selectedTrackIndices.Count > 0 ? _selectedTrackIndices[0] : -1;
        SetField(ref _selectedTrackIndex, selectedIndex, nameof(SelectedTrackIndex));
    }

    public MainWindowViewModel()
    {
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrpheusMP",
            "library.db");

        _musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        // Load persisted settings and state
        var config = ((App)Application.Current!).Config;
        var state = ((App)Application.Current!).State;
        _queueDisplayMode = Enum.TryParse<QueueDisplayMode>(config.QueueDisplayMode, out var qm) ? qm : QueueDisplayMode.Title;
        _showQueueSecondaryText = config.QueueShowSecondaryText;
        _showTitle = config.ShowTitle;
        _showArtist = config.ShowArtist;
        _showAlbum = config.ShowAlbum;
        _showFileName = config.ShowFileName;
        _showLength = config.ShowLength;
        _showFormat = config.ShowFormat;
        _showTrackNumber = config.ShowTrackNumber;
        _showDiscNumber = config.ShowDiscNumber;
        _showYear = config.ShowYear;
        _showGenre = config.ShowGenre;
        _showBitrate = config.ShowBitrate;
        _showLibraryFiles = config.ShowLibraryFiles;
        _sortField = Enum.TryParse<TrackSortField>(config.TrackSortField, out var sortField) ? sortField : TrackSortField.Title;
        _sortAscending = config.TrackSortAscending;
        _hideMissingTitle = config.HideMissingTitle;
        _hideMissingArtist = config.HideMissingArtist;
        _hideMissingAlbum = config.HideMissingAlbum;
        _hideMissingGenre = config.HideMissingGenre;
        _hideMissingTrackNumber = config.HideMissingTrackNumber;
        _volume = state.Volume;

        _library = new SqliteMediaLibrary(_databasePath);
        _scanner = new FolderScanner(_library, new TagLibMetadataReader());
        _scanner.Progress += OnScanProgress;

        var player = new VlcPlayer();
        _controller = new PlayerController(player, state.AudioDevice, _volume);
        _controller.Playlist.Changed += OnPlaylistChanged;
        _controller.Playlist.CurrentIndexChanged += OnPlaylistIndexChanged;
        _controller.ShufflePlayChanged += OnShufflePlayChanged;
        _controller.RepeatModeChanged += OnRepeatModeChanged;
        _controller.StateChanged += OnControllerStateChanged;
        _controller.PositionChanged += OnControllerPositionChanged;
        _controller.VolumeChanged += OnControllerVolumeChanged;


        // Re-tint all icons when the theme/variant changes.
        var app = (App)Application.Current!;
        if (app.ThemeManager is { } tm)
            tm.ThemeChanged += OnThemeChanged;

        // Refresh locale-dependent UI when the language changes at runtime.
        App.LanguageChanged += OnLanguageChanged;

        _ = InitializeAsync();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPauseIconPrimary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviousIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StopIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShuffleIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EqualizerIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlusIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EllipsisIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
        });
    }

    /// <summary>
    /// Refresh all locale-dependent text when the user switches language at runtime.
    /// </summary>
    private void OnLanguageChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Computed properties that use Resources — just raise PropertyChanged
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppTitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatLabel)));

            // Re-compute dynamic summary strings
            QueueSummary = _queue.Count > 0
                ? string.Format(Resources.QueuedSummary, _queue.Count)
                : Resources.QueueEmpty;

            // Re-compute now-playing display
            UpdateNowPlayingDisplay();

            // Rebuild the library tree so folder meta strings are re-localized
            _ = LoadLibraryAsync();
        });
    }

    public async Task AddLibraryFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        await _library.AddWatchedFolderAsync(folder);
        await _scanner.ScanFoldersAsync(new[] { folder });
        await LoadLibraryAsync();
    }

    /// <summary>
    /// Reload the library UI after an external change (e.g. database reset).
    /// </summary>
    public async Task RefreshLibraryAsync()
    {
        await LoadLibraryAsync();
    }

    /// <summary>
    /// Re-scan all watched folders and refresh the library UI.
    /// </summary>
    public async Task RescanAllFoldersAsync()
    {
        await _scanner.ScanAsync();
        await LoadLibraryAsync();
    }

    /// <summary>
    /// Returns the shared VlcEqualizer instance, creating it on first access.
    /// Uses the VLC-specific Player reference for equalizer creation only.
    /// </summary>
    public VlcEqualizer GetEqualizer()
    {
        _equalizer ??= ((VlcPlayer)_controller.Player).CreateEqualizer();
        return _equalizer;
    }

    /// <summary>
    /// Creates a SettingsViewModel wired to this ViewModel's dependencies.
    /// </summary>
    public Views.SettingsViewModel CreateSettingsViewModel(GlobalMediaKeyService? mediaKeyService)
    {
        var app = (App)Application.Current!;
        return new Views.SettingsViewModel(
            app.ThemeManager!,
            app.Config,
            app.State,
            _controller,
            _library,
            onLibraryReset: RefreshLibraryAsync,
            onRescanAll: RescanAllFoldersAsync,
            addLibraryFolder: AddLibraryFolderAsync,
            mediaKeyService: mediaKeyService);
    }

    private async Task InitializeAsync()
    {
        await EnsureDefaultLibraryAsync();
        await LoadLibraryAsync();
        await LoadPlaylistAsync();

        // Scan watched folders in the background so the UI is responsive
        // while the database is brought up to date with any file changes.
        _ = _scanner.ScanAsync();
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
        var isSearch = !string.IsNullOrWhiteSpace(query);

        IsSearchActive = isSearch;

        if (isSearch)
        {
            tracks = await _library.SearchAsync(query!);
        }
        else
        {
            tracks = await _library.GetAllTracksAsync();
        }

        var allTracks = isSearch
            ? await _library.GetAllTracksAsync()
            : tracks;

        _allTracks = allTracks;
        _currentTracks = tracks.Select(ToTrackRow).ToList();
        _viewTracks = _currentTracks;
        _currentPlaylistPath = null;
        IsPlaylistView = false;
        IsTrackOrderDirty = false;
        IsTrackSortEnabled = true;

        var folders = await _library.GetWatchedFoldersAsync();
        var treeNodes = await Task.Run(() => BuildFolderTree(folders, tracks, pruneEmpty: isSearch || tracks.Count == 0, showFiles: _showLibraryFiles));

        var state = ((App)Application.Current!).State;
        var expandedPaths = new HashSet<string>(state.ExpandedPaths, StringComparer.OrdinalIgnoreCase);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MergeNodes(_libraryRoots, treeNodes);
            RestoreExpandedPaths(_libraryRoots, expandedPaths);
            SubscribeToExpansionChanges(_libraryRoots);
            LibrarySummary = string.Format(Resources.FoldersSummary, folders.Count, allTracks.Count);
        });

        await RefreshTrackViewAsync(resetSelection: true);
    }

    private async Task LoadPlaylistAsync()
    {
        var state = ((App)Application.Current!).State;
        var savedPaths = state.QueuePaths;

        if (savedPaths.Count > 0)
        {
            // Restore the play queue from state
            var allTracks = await _library.GetAllTracksAsync();
            var tracksByPath = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in allTracks)
                tracksByPath[t.FilePath] = t;

            var items = new List<PlaylistItem>();
            foreach (var path in savedPaths)
            {
                if (!File.Exists(path))
                    continue;

                PlaylistItem item;
                if (tracksByPath.TryGetValue(path, out var track))
                {
                    item = new PlaylistItem
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
                }
                else
                {
                    // File exists but not in library yet — add with minimal metadata
                    item = new PlaylistItem
                    {
                        Source = MediaSource.FromFile(path),
                    };
                }

                items.Add(item);
            }

            if (items.Count > 0)
            {
                _controller.Playlist.Clear();
                _controller.Playlist.AddRange(items);

                // Restore the current index and position
                var restoredIndex = state.QueueIndex;
                if (restoredIndex >= 0 && restoredIndex < items.Count)
                {
                    _controller.Playlist.CurrentIndex = restoredIndex;

                    // Update display for the restored track
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        UpdateQueueFromPlaylist();
                        CurrentQueueIndex = restoredIndex;

                        var item = _controller.Playlist.CurrentItem;
                        if (item is not null)
                        {
                            NowPlayingTitle = item.Metadata?.Title ?? item.DisplayName;
                            NowPlayingArtist = item.Metadata?.Artist ?? string.Empty;
                            NowPlayingAlbum = item.Metadata?.Album ?? string.Empty;
                            UpdateNowPlayingDisplay();

                            var dur = item.Metadata?.Duration ?? TimeSpan.Zero;
                            var pos = TimeSpan.FromSeconds(state.PlaybackPositionSeconds);
                            _isPositionUpdateFromPlayer = true;
                            PlaybackDuration = dur.TotalSeconds;
                            PlaybackPosition = pos.TotalSeconds;
                            _isPositionUpdateFromPlayer = false;
                            UpdateNowPlayingTime(pos, dur);
                        }
                    });
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
                }

                return;
            }
        }

        // No saved queue (or all tracks gone) — start empty
        _controller.Playlist.Clear();
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
    }

    private void UpdateQueueFromPlaylist()
    {
        _queue.Clear();
        foreach (var item in _controller.Playlist)
        {
            _queue.Add(ToQueueItem(item));
        }

        QueueSummary = string.Format(Resources.QueuedSummary, _queue.Count);
    }

    private void OnScanProgress(object? sender, LibraryScanProgress e)
    {
        if (e.IsComplete)
        {
            // Refresh the library tree but don't touch the play queue — the user
            // may have a restored or manually curated queue we shouldn't overwrite.
            _ = LoadLibraryAsync();
        }
    }

    private void OnPlaylistChanged(object? sender, EventArgs e)
    {
        if (_suppressPlaylistChanged)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateQueueFromPlaylist();
            ScheduleQueueStateSave();
        });
    }

    private void OnPlaylistIndexChanged(object? sender, int index)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CurrentQueueIndex = index;
            var item = _controller.Playlist.CurrentItem;
            if (item is null)
                return;

            NowPlayingTitle = item.Metadata?.Title ?? item.DisplayName;
            NowPlayingArtist = item.Metadata?.Artist ?? string.Empty;
            NowPlayingAlbum = item.Metadata?.Album ?? string.Empty;
            UpdateNowPlayingDisplay();
            // Position/duration will arrive via the next PositionChanged or StateChanged event.
            ScheduleQueueStateSave();
        });
    }

    /// <summary>
    /// Recomputes <see cref="NowPlayingPrimary"/> and <see cref="NowPlayingSecondary"/>
    /// from the current playlist item, respecting QueueDisplayMode and ShowQueueSecondaryText.
    /// </summary>
    private void UpdateNowPlayingDisplay()
    {
        var item = _controller.Playlist.CurrentItem;
        if (item is null)
        {
            NowPlayingPrimary = Resources.NothingPlaying;
            NowPlayingSecondary = "";
            return;
        }

        var metadata = item.Metadata;
        var filePath = item.Source.Type == MediaSourceType.LocalFile
            ? item.Source.Uri.LocalPath
            : item.Source.Uri.ToString();

        ResolveDisplayText(_queueDisplayMode, metadata, filePath, item.DisplayName,
            out var primary, out var secondary);

        NowPlayingPrimary = primary;
        NowPlayingSecondary = _showQueueSecondaryText ? secondary : "";
    }

    /// <summary>
    /// Resolves the primary (title/filename) and secondary (album/folder) display
    /// strings for a playlist item according to the current <see cref="QueueDisplayMode"/>.
    /// </summary>
    private static void ResolveDisplayText(
        QueueDisplayMode mode,
        TrackMetadata? metadata,
        string filePath,
        string fallbackDisplayName,
        out string primary,
        out string secondary)
    {
        var hasTitle  = !string.IsNullOrWhiteSpace(metadata?.Title);
        var hasAlbum  = !string.IsNullOrWhiteSpace(metadata?.Album);
        var fileName  = Path.GetFileNameWithoutExtension(filePath);
        var folder    = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";

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
                primary   = hasTitle  ? metadata!.Title!  : fallbackDisplayName;
                secondary = hasAlbum  ? metadata!.Album!  : "";
                break;
        }
    }

    private void OnControllerPositionChanged(object? sender, PositionSnapshot snap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Ignore stale position updates that arrive after playback has stopped.
            if (!_isActive) return;

            // Don't overwrite the slider while the user is dragging it
            // or during the brief suppression window after a seek
            if (!_isUserSeekingPosition && Environment.TickCount64 >= _seekSuppressUntilTicks)
            {
                _isPositionUpdateFromPlayer = true;
                PlaybackPosition = snap.Position.TotalSeconds;
                _isPositionUpdateFromPlayer = false;
            }
            PlaybackDuration = snap.Duration.TotalSeconds;
            UpdateNowPlayingTime(snap.Position, snap.Duration);
        });
    }

    private void OnControllerStateChanged(object? sender, PlaybackStateSnapshot snap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = snap.IsPlaying;
            IsActive = !snap.IsStopped;
            PlaybackDuration = snap.Duration.TotalSeconds;
            _isPositionUpdateFromPlayer = true;
            if (snap.IsStopped)
            {
                // When stopped, reset position to beginning instead of leaving it at the end
                PlaybackPosition = 0;
                UpdateNowPlayingTime(TimeSpan.Zero, snap.Duration);
            }
            else
            {
                PlaybackPosition = snap.Position.TotalSeconds;
                UpdateNowPlayingTime(snap.Position, snap.Duration);
            }
            _isPositionUpdateFromPlayer = false;
        });
    }

    private void OnControllerVolumeChanged(object? sender, VolumeSnapshot snap)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update local state from the controller's authoritative snapshot
            if (Math.Abs(_volume - snap.Volume) > 0.01)
            {
                var oldVolume = _volume;
                _volume = snap.Volume;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));

                // Re-tint the volume icon when crossing the low/high threshold
                if ((oldVolume < 35) != (snap.Volume < 35))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeIcon)));
            }
            IsMuted = snap.IsMuted;
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
            track.FilePath,
            track.Title ?? Path.GetFileNameWithoutExtension(track.FilePath),
            track.Artist ?? "",
            track.Album ?? "",
            Path.GetFileName(track.FilePath),
            track.TrackNumber?.ToString() ?? "",
            track.DiscNumber?.ToString() ?? "",
            track.Year?.ToString() ?? "",
            track.Genre ?? "",
            track.Bitrate is null ? "" : string.Format(Resources.BitrateFmt, track.Bitrate),
            FormatTime(track.Duration ?? TimeSpan.Zero),
            track.Codec ?? "");
    }

    private QueueItem ToQueueItem(PlaylistItem item)
    {
        var metadata = item.Metadata;
        var filePath = item.Source.Type == MediaSourceType.LocalFile
            ? item.Source.Uri.LocalPath
            : item.Source.Uri.ToString();

        ResolveDisplayText(_queueDisplayMode, metadata, filePath, item.DisplayName,
            out var primaryText, out var secondaryText);

        return new QueueItem(
            primaryText,
            _showQueueSecondaryText ? secondaryText : "",
            FormatTime(metadata?.Duration ?? TimeSpan.Zero));
    }

    private static List<LibraryNode> BuildFolderTree(
        IReadOnlyList<string> folders,
        IReadOnlyList<LibraryTrack> tracks,
        bool pruneEmpty = false,
        bool showFiles = false)
    {
        var counts = BuildTrackCounts(tracks);
        var nodes = new List<LibraryNode>();

        // Build a set of matched file paths so PruneEmptyNodes can filter
        // File/Playlist leaf nodes to only those that are in the search results.
        HashSet<string>? matchedPaths = pruneEmpty
            ? new HashSet<string>(tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var node = BuildFolderNode(folder, counts, showFiles);
            if (pruneEmpty)
                node = PruneEmptyNodes(node, matchedPaths!);
            if (node is not null)
            {
                node.IsExpanded = true;
                nodes.Add(node);
            }
        }

        return nodes;
    }

    /// <summary>
    /// Recursively remove nodes that have no matching tracks.
    /// <para>
    /// File and Playlist leaf nodes are kept only if their path is in
    /// <paramref name="matchedPaths"/> (the search result set).
    /// Folder nodes are kept only if they have a non-zero track count
    /// (i.e. at least one matched track lives beneath them) or at least
    /// one surviving child.
    /// </para>
    /// Returns null if this node itself should be removed.
    /// </summary>
    private static LibraryNode? PruneEmptyNodes(LibraryNode node, HashSet<string> matchedPaths)
    {
        // Leaf nodes (File / Playlist): keep only if they are in the result set.
        if (node.NodeType != LibraryNodeType.Folder)
            return matchedPaths.Contains(node.Path) ? node : null;

        // Folder nodes: recurse first, then decide.
        var prunedChildren = new List<LibraryNode>();
        foreach (var child in node.Children)
        {
            var pruned = PruneEmptyNodes(child, matchedPaths);
            if (pruned is not null)
                prunedChildren.Add(pruned);
        }

        // Keep this folder only if it has matched tracks beneath it OR has
        // surviving children (e.g. subfolders with matched tracks) after pruning.
        if (node.TrackCount == 0 && prunedChildren.Count == 0)
            return null;

        return new LibraryNode(node.Name, node.Meta, node.Path, prunedChildren, trackCount: node.TrackCount);
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

    private static LibraryNode BuildFolderNode(string path, Dictionary<string, int> counts, bool showFiles)
    {
        var children = new List<LibraryNode>();
        if (Directory.Exists(path))
        {
            try
            {
                // Subdirectories
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    children.Add(BuildFolderNode(child, counts, showFiles));
                }

                // Playlist files (.m3u, .m3u8, .pls) — always shown
                foreach (var file in Directory.EnumerateFiles(path)
                             .Where(f => FolderScanner.PlaylistExtensions.Contains(Path.GetExtension(f)))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    children.Add(new LibraryNode(fileName, ext, file, nodeType: LibraryNodeType.Playlist));
                }

                // Audio files — only when ShowLibraryFiles is enabled
                if (showFiles)
                {
                    foreach (var file in Directory.EnumerateFiles(path)
                                 .Where(f => FolderScanner.AudioExtensions.Contains(Path.GetExtension(f)))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var ext = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                        children.Add(new LibraryNode(fileName, ext, file, nodeType: LibraryNodeType.File));
                    }
                }
            }
            catch
            {
            }
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        var trackCount = counts.TryGetValue(path, out var value) ? value : 0;
        var playlistCount = CountPlaylistNodes(children);

        string meta;
        if (!Directory.Exists(path))
        {
            meta = Resources.Missing;
        }
        else if (trackCount > 0 && playlistCount > 0)
        {
            meta = string.Format(Resources.TracksAndPlaylists, trackCount, playlistCount);
        }
        else if (trackCount > 0)
        {
            meta = string.Format(Resources.TrackCount, trackCount);
        }
        else if (playlistCount > 0)
        {
            meta = string.Format(Resources.PlaylistCount, playlistCount);
        }
        else
        {
            meta = Resources.Empty;
        }

        return new LibraryNode(name, meta, path, children, trackCount: trackCount);
    }

    /// <summary>
    /// Recursively count Playlist-type nodes under a list of children.
    /// </summary>
    private static int CountPlaylistNodes(IReadOnlyList<LibraryNode> children)
    {
        var count = 0;
        foreach (var child in children)
        {
            if (child.NodeType == LibraryNodeType.Playlist)
                count++;
            else if (child.NodeType == LibraryNodeType.Folder)
                count += CountPlaylistNodes(child.Children);
        }
        return count;
    }

    /// <summary>
    /// Recursively merge new tree nodes into an existing ObservableCollection,
    /// updating Meta in-place and only adding/removing nodes as needed.
    /// This preserves the TreeView's expansion state.
    /// </summary>
    private static void MergeNodes(ObservableCollection<LibraryNode> existing, IReadOnlyList<LibraryNode> incoming)
    {
        // Build lookup of incoming nodes by path for fast matching.
        var incomingByPath = new Dictionary<string, LibraryNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in incoming)
            incomingByPath[node.Path] = node;

        // Remove nodes that are no longer present.
        for (var i = existing.Count - 1; i >= 0; i--)
        {
            if (!incomingByPath.ContainsKey(existing[i].Path))
                existing.RemoveAt(i);
        }

        // Update existing nodes and insert new ones in the correct order.
        for (var i = 0; i < incoming.Count; i++)
        {
            var source = incoming[i];

            if (i < existing.Count && string.Equals(existing[i].Path, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                // Same node — update meta and recurse into children.
                existing[i].Meta = source.Meta;
                MergeNodes(existing[i].Children, (IReadOnlyList<LibraryNode>)source.Children);
            }
            else
            {
                // Find whether this node exists further in the list.
                var found = false;
                for (var j = i + 1; j < existing.Count; j++)
                {
                    if (string.Equals(existing[j].Path, source.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        // Move it into position.
                        existing.Move(j, i);
                        existing[i].Meta = source.Meta;
                        MergeNodes(existing[i].Children, (IReadOnlyList<LibraryNode>)source.Children);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // New node — insert at the correct position.
                    existing.Insert(i, source);
                }
            }
        }
    }

    // ── Tree expansion state persistence ────────────────────

    /// <summary>
    /// Walk the tree and set IsExpanded for nodes whose Path is in the saved set.
    /// Root nodes default to expanded if they have no saved state.
    /// </summary>
    private static void RestoreExpandedPaths(
        ObservableCollection<LibraryNode> roots,
        HashSet<string> expandedPaths)
    {
        if (expandedPaths.Count == 0)
        {
            // No saved state — keep root nodes expanded (default behavior)
            return;
        }

        foreach (var root in roots)
            RestoreExpandedPathsRecursive(root, expandedPaths);
    }

    private static void RestoreExpandedPathsRecursive(LibraryNode node, HashSet<string> expandedPaths)
    {
        node.IsExpanded = expandedPaths.Contains(node.Path);
        foreach (var child in node.Children)
            RestoreExpandedPathsRecursive(child, expandedPaths);
    }

    /// <summary>
    /// Subscribe to IsExpanded changes on all tree nodes so we can persist state.
    /// </summary>
    private void SubscribeToExpansionChanges(ObservableCollection<LibraryNode> roots)
    {
        foreach (var root in roots)
            SubscribeToExpansionChangesRecursive(root);
    }

    private void SubscribeToExpansionChangesRecursive(LibraryNode node)
    {
        // Avoid duplicate subscriptions by unsubscribing first
        node.PropertyChanged -= OnLibraryNodePropertyChanged;
        node.PropertyChanged += OnLibraryNodePropertyChanged;

        foreach (var child in node.Children)
            SubscribeToExpansionChangesRecursive(child);
    }

    private void OnLibraryNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryNode.IsExpanded))
            return;

        // Debounce: save after a short delay so rapid expand/collapse doesn't thrash disk
        _expandedPathsSaveTimer?.Stop();
        _expandedPathsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _expandedPathsSaveTimer.Tick += (_, _) =>
        {
            _expandedPathsSaveTimer.Stop();
            SaveExpandedPaths();
        };
        _expandedPathsSaveTimer.Start();
    }

    private void SaveExpandedPaths()
    {
        var expanded = new List<string>();
        CollectExpandedPaths(_libraryRoots, expanded);

        var state = ((App)Application.Current!).State;
        state.ExpandedPaths = expanded;
        state.Save();
    }

    private static void CollectExpandedPaths(IEnumerable<LibraryNode> nodes, List<string> paths)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
                paths.Add(node.Path);
            CollectExpandedPaths(node.Children, paths);
        }
    }

    // ── Queue / playback state persistence ───────────────────

    /// <summary>
    /// Debounced save of queue state (paths, current index, playback position).
    /// </summary>
    private void ScheduleQueueStateSave()
    {
        _queueStateSaveTimer?.Stop();
        _queueStateSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _queueStateSaveTimer.Tick += (_, _) =>
        {
            _queueStateSaveTimer.Stop();
            SaveQueueState();
        };
        _queueStateSaveTimer.Start();
    }

    private void SaveQueueState()
    {
        var state = ((App)Application.Current!).State;

        var paths = new List<string>();
        foreach (var item in _controller.Playlist)
        {
            if (item.Source.Type == MediaSourceType.LocalFile)
                paths.Add(item.Source.Uri.LocalPath);
            else
                paths.Add(item.Source.Uri.ToString());
        }

        state.QueuePaths = paths;
        state.QueueIndex = _controller.Playlist.CurrentIndex;
        state.PlaybackPositionSeconds = _playbackPosition;
        state.Save();
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
        _controller.ToggleShufflePlay();
    }

    public void SetShuffle(bool enabled)
    {
        _controller.SetShufflePlay(enabled);
    }

    public void CycleRepeat()
    {
        _controller.CycleRepeatMode();
    }

    /// <summary>
    /// Sets the repeat mode from an MPRIS LoopStatus string
    /// ("None", "Track", or "Playlist").
    /// </summary>
    public void SetLoopStatus(string loopStatus)
    {
        var mode = loopStatus switch
        {
            "Track"    => Orpheus.Core.Playlist.RepeatMode.One,
            "Playlist" => Orpheus.Core.Playlist.RepeatMode.All,
            _          => Orpheus.Core.Playlist.RepeatMode.Off,
        };
        _controller.SetRepeatMode(mode);
    }

    public async Task PlaySelectedAsync()
    {
        if (_selectedTrackIndex < 0)
            return;

        if (!await ConfirmQueueReplaceAsync())
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
        IsQueueDirty = false;
    }

    public async Task SelectFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        var filteredTracks = await Task.Run(() =>
            _allTracks.Where(t => IsUnderFolder(t.FilePath, folderPath)).Select(ToTrackRow).ToList());

        _currentTracks = filteredTracks;
        _currentPlaylistPath = null;
        IsPlaylistView = false;
        IsTrackOrderDirty = false;
        IsTrackSortEnabled = true;
        await RefreshTrackViewAsync(resetSelection: true);
    }

    /// <summary>
    /// Show the contents of a playlist file in the track list panel.
    /// Entries that are in the library use their cached metadata; entries not
    /// yet indexed fall back to a stub track so they still appear in the list.
    /// Playlist order is preserved.
    /// </summary>
    public async Task SelectPlaylistAsync(string playlistPath)
    {
        if (string.IsNullOrWhiteSpace(playlistPath))
            return;

        var tracks = await Task.Run(() =>
        {
            IReadOnlyList<PlaylistItem> items;
            try
            {
                items = PlaylistFileReader.ReadFile(playlistPath);
            }
            catch
            {
                return new List<TrackRow>();
            }

            var byPath = _allTracks.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);

            var result = new List<TrackRow>(items.Count);
            foreach (var item in items)
            {
                if (item.Source.Type != MediaSourceType.LocalFile)
                    continue;

                var filePath = item.Source.Uri.LocalPath;

                if (byPath.TryGetValue(filePath, out var libraryTrack))
                {
                    result.Add(ToTrackRow(libraryTrack));
                }
                else if (File.Exists(filePath))
                {
                    // File exists but is not in the library — create a stub so
                    // it still shows up in the track list.
                    result.Add(ToTrackRow(new LibraryTrack
                    {
                        FilePath = filePath,
                        Title    = item.Metadata?.Title
                                   ?? Path.GetFileNameWithoutExtension(filePath),
                        Artist   = item.Metadata?.Artist,
                        Album    = item.Metadata?.Album,
                    }));
                }
            }
            return result;
        });

        _currentTracks = tracks;
        _currentPlaylistPath = playlistPath;
        IsPlaylistView = true;
        IsTrackOrderDirty = false;
        IsTrackSortEnabled = false;
        await RefreshTrackViewAsync(resetSelection: true);
    }

    /// <summary>
    /// Move a queue item from one position to another (for drag-and-drop reorder).
    /// </summary>
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _queue.Count ||
            toIndex < 0 || toIndex >= _queue.Count ||
            fromIndex == toIndex)
            return;

        // Suppress the Playlist.Changed → UpdateQueueFromPlaylist round-trip
        // so the ObservableCollection move is the only UI mutation during drag.
        _suppressPlaylistChanged = true;
        try
        {
            _controller.Playlist.Move(fromIndex, toIndex);
        }
        finally
        {
            _suppressPlaylistChanged = false;
        }

        _queue.Move(fromIndex, toIndex);

        // Update the current queue index display
        CurrentQueueIndex = _controller.Playlist.CurrentIndex;
    }

    // ── Visual drag-and-drop with placeholder ────────────────

    /// <summary>Sentinel QueueItem that renders as an empty placeholder gap.</summary>
    private static readonly QueueItem DragPlaceholder = new("", "", "", IsPlaceholder: true);

    /// <summary>
    /// Begins a visual drag: removes the real item from the queue and inserts
    /// a placeholder at its position.  The real item is held by the caller
    /// and shown in a floating adorner.
    /// </summary>
    public void BeginDragQueueItem(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;

        _suppressPlaylistChanged = true;
        try
        {
            _controller.Playlist.Move(index, index); // no-op — keeps playlist in sync later
        }
        finally
        {
            _suppressPlaylistChanged = false;
        }

        _queue[index] = DragPlaceholder;
    }

    /// <summary>
    /// Moves the placeholder from one position to another during a drag,
    /// keeping the underlying playlist in sync.
    /// </summary>
    public void MoveDragPlaceholder(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _queue.Count ||
            toIndex < 0 || toIndex >= _queue.Count ||
            fromIndex == toIndex)
            return;

        _suppressPlaylistChanged = true;
        try
        {
            _controller.Playlist.Move(fromIndex, toIndex);
        }
        finally
        {
            _suppressPlaylistChanged = false;
        }

        _queue.Move(fromIndex, toIndex);
        CurrentQueueIndex = _controller.Playlist.CurrentIndex;
    }

    /// <summary>
    /// Completes the drag: replaces the placeholder with the real item at the
    /// final drop position.
    /// </summary>
    public void EndDragQueueItem(int placeholderIndex, QueueItem realItem)
    {
        if (placeholderIndex < 0 || placeholderIndex >= _queue.Count)
            return;

        _queue[placeholderIndex] = realItem;
        CurrentQueueIndex = _controller.Playlist.CurrentIndex;
        IsQueueDirty = true;
    }

    // ── Cross-panel drop placeholder ─────────────────────────

    private int _dropPlaceholderIndex = -1;

    /// <summary>Current index of the cross-panel drop placeholder, or -1.</summary>
    public int DropPlaceholderIndex => _dropPlaceholderIndex;

    /// <summary>
    /// Insert a visual-only placeholder into the queue at the given index.
    /// Does NOT touch the underlying Playlist — this is purely for visual
    /// feedback during a cross-panel drag.
    /// </summary>
    public void InsertDropPlaceholder(int index)
    {
        if (index < 0) index = _queue.Count;
        index = Math.Min(index, _queue.Count);

        if (_dropPlaceholderIndex >= 0)
        {
            // Already have one — just move it
            MoveDropPlaceholder(index);
            return;
        }

        _queue.Insert(index, DragPlaceholder);
        _dropPlaceholderIndex = index;
    }

    /// <summary>
    /// Move the cross-panel drop placeholder to a new index.
    /// </summary>
    public void MoveDropPlaceholder(int newIndex)
    {
        if (_dropPlaceholderIndex < 0 || _dropPlaceholderIndex >= _queue.Count)
            return;

        if (newIndex < 0) newIndex = _queue.Count - 1;
        newIndex = Math.Min(newIndex, _queue.Count - 1);

        if (newIndex == _dropPlaceholderIndex)
            return;

        _queue.Move(_dropPlaceholderIndex, newIndex);
        _dropPlaceholderIndex = newIndex;
    }

    /// <summary>
    /// Remove the cross-panel drop placeholder.
    /// Returns the index it was at (for use as insertion point), or -1.
    /// </summary>
    public int RemoveDropPlaceholder()
    {
        if (_dropPlaceholderIndex < 0 || _dropPlaceholderIndex >= _queue.Count)
        {
            _dropPlaceholderIndex = -1;
            return -1;
        }

        var index = _dropPlaceholderIndex;
        _queue.RemoveAt(index);
        _dropPlaceholderIndex = -1;
        return index;
    }

    /// <summary>
    /// Skip to and play the queue item at the given index.
    /// </summary>
    public async Task PlayQueueItemAsync(int index)
    {
        if (index < 0 || index >= _controller.Playlist.Count)
            return;

        await _controller.PlayAtIndexAsync(index).ConfigureAwait(false);
    }

    public async Task PlayFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        if (!await ConfirmQueueReplaceAsync())
            return;

        var folderTracks = await Task.Run(() =>
            _allTracks.Where(t => IsUnderFolder(t.FilePath, folderPath)).Select(ToTrackRow).ToList());

        if (folderTracks.Count == 0)
            return;

        // Update the track list to show this folder's tracks
        _currentTracks = folderTracks;
        await RefreshTrackViewAsync(resetSelection: true);

        // Build playlist and load into play queue
        var (items, _) = await Task.Run(() => BuildPlaylistItems(folderTracks, null));
        if (items.Count == 0)
            return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
        IsQueueDirty = false;
    }

    /// <summary>
    /// Play a single audio file by loading it into the queue.
    /// </summary>
    public async Task PlayFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        if (!await ConfirmQueueReplaceAsync())
            return;

        var item = new PlaylistItem
        {
            Source = MediaSource.FromFile(filePath)
        };

        // Try to find matching metadata from the library
        var track = _allTracks.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (track is not null)
        {
            item.Metadata = new TrackMetadata
            {
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Duration = track.Duration
            };
        }

        _controller.Playlist.Clear();
        _controller.Playlist.Add(item);
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
        IsQueueDirty = false;
    }

    /// <summary>
    /// Load a playlist file (.m3u, .m3u8, .pls) via PlaylistFileReader
    /// and play its contents.
    /// </summary>
    public async Task PlayPlaylistFileAsync(string playlistPath)
    {
        if (string.IsNullOrWhiteSpace(playlistPath) || !File.Exists(playlistPath))
            return;

        if (!await ConfirmQueueReplaceAsync())
            return;

        var items = await Task.Run(() =>
        {
            try { return PlaylistFileReader.ReadFile(playlistPath); }
            catch { return Array.Empty<PlaylistItem>(); }
        });

        if (items.Count == 0)
            return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
        IsQueueDirty = false;
    }

    // ── Internet radio / stream URL playback ──────────────────

    /// <summary>
    /// Returns true if <paramref name="text"/> looks like a URL that should be
    /// treated as a radio/stream source rather than a library search query.
    /// </summary>
    public static bool IsRadioUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "rtsp" or "rtp" or "mms" or "mmsh";
    }

    /// <summary>
    /// Plays a URL directly. Supports:
    ///  • Direct audio stream URLs (played immediately)
    ///  • Remote .m3u/.m3u8/.pls playlist URLs (fetched, parsed, then played)
    /// Updates <see cref="SessionMode"/> to reflect the new source type.
    /// </summary>
    public async Task PlayRadioUrlAsync(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (!await ConfirmQueueReplaceAsync()) return;

        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        IReadOnlyList<PlaylistItem> items;

        if (ext is ".m3u" or ".m3u8" or ".pls")
        {
            // Fetch and parse the remote playlist.
            items = await Task.Run(async () =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(10);
                    var content = await http.GetStringAsync(uri).ConfigureAwait(false);
                    return ParseRemotePlaylist(content, ext, url);
                }
                catch
                {
                    return Array.Empty<PlaylistItem>();
                }
            });
        }
        else
        {
            // Treat as a direct stream/radio URL.
            var source = MediaSource.FromUri(uri);
            source.DisplayName = uri.Host;
            items = new[] { new PlaylistItem { Source = source } };
        }

        if (items.Count == 0) return;

        _controller.Playlist.Clear();
        _controller.Playlist.AddRange(items);

        // Determine session mode from the first item's source type.
        var firstType = items[0].Source.Type;
        SessionMode = firstType == MediaSourceType.InternetRadio
            ? global::Orpheus.Desktop.SessionMode.Radio
            : global::Orpheus.Desktop.SessionMode.Stream;

        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        await _controller.PlayAtIndexAsync(0).ConfigureAwait(false);
        IsQueueDirty = false;

        // Clear the search box so normal library search is restored.
        _searchQuery = string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchQuery)));
    }

    private static IReadOnlyList<PlaylistItem> ParseRemotePlaylist(string content, string ext, string sourceUrl)
    {
        // Write to a temp file so the existing PlaylistFileReader can parse it.
        var tmp = Path.Combine(Path.GetTempPath(), $"orpheus_radio_{Guid.NewGuid()}{ext}");
        try
        {
            File.WriteAllText(tmp, content);
            return PlaylistFileReader.ReadFile(tmp);
        }
        catch
        {
            return Array.Empty<PlaylistItem>();
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    // ── Queue manipulation (add / remove / save / confirm) ────

    /// <summary>
    /// Prompts the user to Save, Discard, or Cancel when the queue is dirty
    /// and an action would replace it. Returns false if the caller should abort.
    /// </summary>
    public async Task<bool> ConfirmQueueReplaceAsync()
    {
        if (!_isQueueDirty || _queue.Count == 0)
            return true;

        var result = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = Application.Current!.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            if (window is null) return QueueConfirmResult.Discard;

            var dialog = new QueueConfirmDialog();
            return await dialog.ShowDialog(window);
        });

        switch (result)
        {
            case QueueConfirmResult.Save:
                await SaveQueueAsPlaylistAsync();
                return true;
            case QueueConfirmResult.Discard:
                IsQueueDirty = false;
                return true;
            case QueueConfirmResult.Cancel:
            default:
                return false;
        }
    }

    /// <summary>
    /// Opens a file picker and saves the current play queue as a playlist file.
    /// </summary>
    public async Task SaveQueueAsPlaylistAsync()
    {
        if (_controller.Playlist.Count == 0)
            return;

        var window = Application.Current!.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (window is null) return;

        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return;

        var result = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = Resources.SaveQueueAsPlaylist,
            SuggestedFileName = "playlist",
            DefaultExtension = "m3u",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType(Resources.M3UPlaylist) { Patterns = new[] { "*.m3u" } },
                new Avalonia.Platform.Storage.FilePickerFileType(Resources.PLSPlaylist) { Patterns = new[] { "*.pls" } },
            }
        });

        var path = result?.Path?.LocalPath ?? result?.Path?.ToString();
        if (string.IsNullOrWhiteSpace(path))
            return;

        await Task.Run(() => PlaylistFileWriter.WriteFile(_controller.Playlist, path));
        IsQueueDirty = false;
    }

    public async Task SaveTrackOrderAsync()
    {
        if (!CanSaveTrackOrder || string.IsNullOrWhiteSpace(_currentPlaylistPath))
            return;

        var playlistPath = _currentPlaylistPath;
        var items = await Task.Run(() => BuildPlaylistItems(_currentTracks, null).Items);
        if (items.Count == 0)
            return;

        await Task.Run(() =>
        {
            var playlist = new Playlist();
            playlist.AddRange(items);
            PlaylistFileWriter.WriteFile(playlist, playlistPath);
        });

        IsTrackOrderDirty = false;
    }

    /// <summary>
    /// Append tracks (from the library/track list) to the end of the play queue.
    /// </summary>
    /// <summary>
    /// Append the track at the given view index to the play queue (for drag-from-track-list).
    /// </summary>
    public async Task AddSelectedTrackToQueueAsync(int viewIndex, int insertAt = -1)
    {
        var viewTracks = _viewTracks.Count > 0 ? _viewTracks : _currentTracks;
        if (viewIndex < 0 || viewIndex >= viewTracks.Count) return;

        await AddTracksToQueueAsync(new[] { viewTracks[viewIndex] }, insertAt);
    }

    public async Task AddTracksToQueueAsync(IReadOnlyList<TrackRow> tracks, int insertAt = -1)
    {
        if (tracks.Count == 0) return;

        var (items, _) = await Task.Run(() => BuildPlaylistItems(tracks, null));
        if (items.Count == 0) return;

        _suppressPlaylistChanged = true;
        try
        {
            if (insertAt >= 0 && insertAt <= _controller.Playlist.Count)
                _controller.Playlist.InsertRange(insertAt, items);
            else
                _controller.Playlist.AddRange(items);
        }
        finally
        {
            _suppressPlaylistChanged = false;
        }

        // Determine starting index in the playlist for these items
        var startIdx = (insertAt >= 0 && insertAt <= _queue.Count) ? insertAt : _queue.Count;
        for (var i = 0; i < items.Count; i++)
            _queue.Insert(startIdx + i, ToQueueItem(items[i]));

        // If the insertion shifted the current playing index, update it
        if (insertAt >= 0 && insertAt <= CurrentQueueIndex && CurrentQueueIndex >= 0)
            CurrentQueueIndex += items.Count;

        QueueSummary = string.Format(Resources.QueuedSummary, _queue.Count);
        IsQueueDirty = true;
        ScheduleQueueStateSave();
    }

    public async Task AddSelectedTracksToQueueAsync(IReadOnlyList<int> viewIndices, int insertAt = -1)
    {
        if (viewIndices.Count == 0)
            return;

        var viewTracks = _viewTracks.Count > 0 ? _viewTracks : _currentTracks;
        var tracks = viewIndices
            .Where(index => index >= 0 && index < viewTracks.Count)
            .Distinct()
            .OrderBy(index => index)
            .Select(index => viewTracks[index])
            .ToList();

        await AddTracksToQueueAsync(tracks, insertAt);
    }

    /// <summary>
    /// Add a single audio file to the play queue at the specified position,
    /// or append if insertAt is -1.
    /// </summary>
    public void AddFileToQueue(string filePath, int insertAt = -1)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var item = new PlaylistItem { Source = MediaSource.FromFile(filePath) };
        var track = _allTracks.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (track is not null)
        {
            item.Metadata = new TrackMetadata
            {
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Duration = track.Duration
            };
        }

        _suppressPlaylistChanged = true;
        try
        {
            if (insertAt >= 0 && insertAt <= _controller.Playlist.Count)
                _controller.Playlist.Insert(insertAt, item);
            else
                _controller.Playlist.Add(item);
        }
        finally { _suppressPlaylistChanged = false; }

        if (insertAt >= 0 && insertAt <= _queue.Count)
        {
            _queue.Insert(insertAt, ToQueueItem(item));
            if (insertAt <= CurrentQueueIndex && CurrentQueueIndex >= 0)
                CurrentQueueIndex++;
        }
        else
        {
            _queue.Add(ToQueueItem(item));
        }

        QueueSummary = string.Format(Resources.QueuedSummary, _queue.Count);
        IsQueueDirty = true;
        ScheduleQueueStateSave();
    }

    /// <summary>
    /// Add all entries from a playlist file to the play queue at the specified
    /// position, or append if insertAt is -1.
    /// </summary>
    public async Task AddPlaylistFileToQueueAsync(string playlistPath, int insertAt = -1)
    {
        if (string.IsNullOrWhiteSpace(playlistPath) || !File.Exists(playlistPath))
            return;

        var items = await Task.Run(() =>
        {
            try { return PlaylistFileReader.ReadFile(playlistPath); }
            catch { return Array.Empty<PlaylistItem>(); }
        });

        if (items.Count == 0) return;

        _suppressPlaylistChanged = true;
        try
        {
            if (insertAt >= 0 && insertAt <= _controller.Playlist.Count)
                _controller.Playlist.InsertRange(insertAt, items);
            else
                _controller.Playlist.AddRange(items);
        }
        finally { _suppressPlaylistChanged = false; }

        var startIdx = (insertAt >= 0 && insertAt <= _queue.Count) ? insertAt : _queue.Count;
        for (var i = 0; i < items.Count; i++)
            _queue.Insert(startIdx + i, ToQueueItem(items[i]));

        if (insertAt >= 0 && insertAt <= CurrentQueueIndex && CurrentQueueIndex >= 0)
            CurrentQueueIndex += items.Count;

        QueueSummary = string.Format(Resources.QueuedSummary, _queue.Count);
        IsQueueDirty = true;
        ScheduleQueueStateSave();
    }

    /// <summary>
    /// Add all tracks under a folder to the play queue at the specified
    /// position, or append if insertAt is -1.
    /// </summary>
    public async Task AddFolderToQueueAsync(string folderPath, int insertAt = -1)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        var folderTracks = await Task.Run(() =>
            _allTracks.Where(t => IsUnderFolder(t.FilePath, folderPath)).Select(ToTrackRow).ToList());

        if (folderTracks.Count == 0) return;

        await AddTracksToQueueAsync(folderTracks, insertAt);
    }

    /// <summary>
    /// Remove the queue item at the given index.
    /// </summary>
    public void RemoveFromQueue(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;

        _suppressPlaylistChanged = true;
        try { _controller.Playlist.RemoveAt(index); }
        finally { _suppressPlaylistChanged = false; }

        _queue.RemoveAt(index);
        CurrentQueueIndex = _controller.Playlist.CurrentIndex;
        QueueSummary = string.Format(Resources.QueuedSummary, _queue.Count);
        IsQueueDirty = true;
        ScheduleQueueStateSave();
    }

    /// <summary>
    /// Clear the entire play queue (with confirmation if dirty).
    /// </summary>
    public async Task ClearQueueAsync()
    {
        if (!await ConfirmQueueReplaceAsync())
            return;

        await _controller.StopAsync().ConfigureAwait(false);
        _controller.Playlist.Clear();
        await Dispatcher.UIThread.InvokeAsync(UpdateQueueFromPlaylist);
        IsQueueDirty = false;
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        var folderFull = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fileFull = Path.GetFullPath(filePath);
        return fileFull.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase);
    }

    private static (List<PlaylistItem> Items, int SelectedIndex) BuildPlaylistItems(
        IReadOnlyList<TrackRow> tracks,
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
                        Title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title,
                        Artist = string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist,
                        Album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
                        Duration = TryParseTrackLength(track.Length)
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

    private static TimeSpan? TryParseTrackLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return TimeSpan.TryParseExact(value, new[] { @"m\:ss", @"h\:mm\:ss" }, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : null;
    }

    private void SaveConfig()
    {
        var app = (App)Application.Current!;

        var config = app.Config;
        config.QueueDisplayMode = _queueDisplayMode.ToString();
        config.QueueShowSecondaryText = _showQueueSecondaryText;
        config.ShowTitle = _showTitle;
        config.ShowArtist = _showArtist;
        config.ShowAlbum = _showAlbum;
        config.ShowFileName = _showFileName;
        config.ShowLength = _showLength;
        config.ShowFormat = _showFormat;
        config.ShowTrackNumber = _showTrackNumber;
        config.ShowDiscNumber = _showDiscNumber;
        config.ShowYear = _showYear;
        config.ShowGenre = _showGenre;
        config.ShowBitrate = _showBitrate;
        config.ShowLibraryFiles = _showLibraryFiles;
        config.TrackSortField = _sortField.ToString();
        config.TrackSortAscending = _sortAscending;
        config.HideMissingTitle = _hideMissingTitle;
        config.HideMissingArtist = _hideMissingArtist;
        config.HideMissingAlbum = _hideMissingAlbum;
        config.HideMissingGenre = _hideMissingGenre;
        config.HideMissingTrackNumber = _hideMissingTrackNumber;
        config.Save();

        var state = app.State;
        state.Volume = _volume;
        state.Save();
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
        // Persist queue and playback state before tearing down
        SaveQueueState();

        App.LanguageChanged -= OnLanguageChanged;
        if (((App)Application.Current!).ThemeManager is { } tm)
            tm.ThemeChanged -= OnThemeChanged;
        _scanner.Progress -= OnScanProgress;
        _controller.Playlist.Changed -= OnPlaylistChanged;
        _controller.Playlist.CurrentIndexChanged -= OnPlaylistIndexChanged;
        _controller.ShufflePlayChanged -= OnShufflePlayChanged;
        _controller.RepeatModeChanged -= OnRepeatModeChanged;
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.PositionChanged -= OnControllerPositionChanged;
        _controller.VolumeChanged -= OnControllerVolumeChanged;
        _equalizer?.Dispose();
        await _controller.DisposeAsync().ConfigureAwait(false);
        _library.Dispose();
    }

#if LINUX
    /// <summary>
    /// Builds an <see cref="MprisPlayerState"/> snapshot from the current
    /// ViewModel state, used to push updates to the MPRIS D-Bus service.
    /// </summary>
    internal MprisPlayerState BuildMprisState()
    {
        // Convert the OrpheusMP RepeatMode enum to the MPRIS LoopStatus string.
        string loopStatus = _repeatMode switch
        {
            Orpheus.Core.Playlist.RepeatMode.One => "Track",
            Orpheus.Core.Playlist.RepeatMode.All => "Playlist",
            _                                    => "None",
        };

        // Build a stable object path from the queue index so MPRIS clients
        // can track individual tracks.
        string? trackId = _currentQueueIndex >= 0
            ? $"/org/orpheusmp/track/{_currentQueueIndex}"
            : null;

        return new MprisPlayerState
        {
            IsPlaying        = _isPlaying,
            IsActive         = _isActive,
            IsShuffleEnabled = _isShuffleEnabled,
            RepeatMode       = loopStatus,
            Volume           = _volume,
            PositionSeconds  = _playbackPosition,
            DurationSeconds  = _playbackDuration,
            // Use the display-mode-aware primary/secondary text so that when the
            // user has "Show Filename" enabled, MPRIS clients (KDE widget, etc.)
            // see the filename stem rather than the raw (possibly empty) metadata title.
            Title            = _nowPlayingPrimary == Lang.Resources.NothingPlaying ? null : _nowPlayingPrimary,
            Artist           = string.IsNullOrEmpty(_nowPlayingSecondary) ? null : _nowPlayingSecondary,
            Album            = string.IsNullOrEmpty(_nowPlayingAlbum)     ? null : _nowPlayingAlbum,
            TrackId          = trackId,
        };
    }
#endif

    private async Task RefreshTrackViewAsync(bool resetSelection = false)
    {
        var view = await Task.Run(() => BuildTrackView(_currentTracks));
        _viewTracks = view.Tracks;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _tracks.Clear();
            foreach (var row in view.Rows)
                _tracks.Add(row);

            if (resetSelection)
            {
                SetSelectedTrackIndices(Array.Empty<int>());
            }
            else
            {
                SetSelectedTrackIndices(_selectedTrackIndices);
            }
        });
    }

    private TrackView BuildTrackView(IReadOnlyList<TrackRow> source)
    {
        IEnumerable<TrackRow> query = source;

        // Filters and sort are suppressed during search — results are already
        // ordered by relevance and filters would hide valid matches.
        if (!IsSearchActive)
        {
            if (HideMissingTitle)
                query = query.Where(t => !string.IsNullOrWhiteSpace(t.Title));
            if (HideMissingArtist)
                query = query.Where(t => !string.IsNullOrWhiteSpace(t.Artist));
            if (HideMissingAlbum)
                query = query.Where(t => !string.IsNullOrWhiteSpace(t.Album));
            if (HideMissingGenre)
                query = query.Where(t => !string.IsNullOrWhiteSpace(t.Genre));
            if (HideMissingTrackNumber)
                query = query.Where(t => int.TryParse(t.TrackNumber, out var trackNumber) && trackNumber > 0);

            if (IsTrackSortEnabled)
                query = ApplySort(query);
        }

        var list = query.ToList();
        return new TrackView(list, list);
    }

    private IEnumerable<TrackRow> ApplySort(IEnumerable<TrackRow> tracks)
    {
        return SortField switch
        {
            TrackSortField.Title => OrderByString(tracks, t => t.Title, SortAscending),
            TrackSortField.Artist => OrderByString(tracks, t => t.Artist, SortAscending),
            TrackSortField.Album => OrderByString(tracks, t => t.Album, SortAscending),
            TrackSortField.FileName => OrderByString(tracks, t => t.FileName, SortAscending),
            TrackSortField.TrackNumber => OrderByNumber(tracks, t => ParseSortableUInt(t.TrackNumber), SortAscending),
            TrackSortField.Year => OrderByNumber(tracks, t => ParseSortableUInt(t.Year), SortAscending),
            TrackSortField.Duration => OrderByNumber(tracks, t => ParseSortableDuration(t.Length), SortAscending),
            TrackSortField.DateAdded => tracks,
            TrackSortField.Bitrate => OrderByNumber(tracks, t => ParseSortableInt(t.Bitrate), SortAscending),
            _ => OrderByString(tracks, t => t.Title, SortAscending)
        };
    }

    private static IEnumerable<TrackRow> OrderByString(
        IEnumerable<TrackRow> tracks,
        Func<TrackRow, string?> selector,
        bool ascending)
    {
        return ascending
            ? tracks.OrderBy(t => selector(t) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : tracks.OrderByDescending(t => selector(t) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<TrackRow> OrderByNumber<T>(
        IEnumerable<TrackRow> tracks,
        Func<TrackRow, T> selector,
        bool ascending) where T : IComparable<T>
    {
        return ascending
            ? tracks.OrderBy(selector)
            : tracks.OrderByDescending(selector);
    }

    private static uint ParseSortableUInt(string? value) => uint.TryParse(value, out var number) ? number : uint.MaxValue;

    private static int ParseSortableInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return int.MaxValue;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    private static long ParseSortableDuration(string? value)
    {
        var duration = TryParseTrackLength(value);
        return duration?.Ticks ?? long.MaxValue;
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
        IsTrackSortEnabled = true;

        if (SortField == field)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortField = field;
            SortAscending = true;
        }

        if (IsPlaylistView)
            IsTrackOrderDirty = false;

        SaveConfig();
        _ = RefreshTrackViewAsync();
    }

    public void ToggleSortDirection()
    {
        if (!IsTrackSortEnabled)
            return;

        SortAscending = !SortAscending;
        SaveConfig();
        _ = RefreshTrackViewAsync();
    }

    public void MoveTrackRows(IReadOnlyList<int> selectedViewIndices, int targetIndex)
    {
        if (selectedViewIndices.Count == 0)
            return;

        var normalized = selectedViewIndices
            .Where(index => index >= 0 && index < _viewTracks.Count)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        if (normalized.Count == 0)
            return;

        var displayRows = _viewTracks.ToList();
        var movingRows = normalized.Select(index => displayRows[index]).ToList();
        foreach (var index in normalized.OrderByDescending(index => index))
            displayRows.RemoveAt(index);

        var insertionIndex = Math.Clamp(targetIndex, 0, displayRows.Count);
        var removedBefore = normalized.Count(index => index < insertionIndex);
        insertionIndex -= removedBefore;
        insertionIndex = Math.Clamp(insertionIndex, 0, displayRows.Count);

        foreach (var row in movingRows)
        {
            displayRows.Insert(insertionIndex, row);
            insertionIndex++;
        }

        _viewTracks = displayRows;

        var currentRows = _currentTracks.ToList();
        var movingPaths = new HashSet<string>(movingRows.Select(row => row.FilePath), StringComparer.OrdinalIgnoreCase);
        var remainingRows = currentRows.Where(row => !movingPaths.Contains(row.FilePath)).ToList();

        var firstAnchorIndex = normalized[0];
        TrackRow? anchorRow = null;
        if (firstAnchorIndex < displayRows.Count)
            anchorRow = displayRows[firstAnchorIndex];
        else if (displayRows.Count > 0)
            anchorRow = displayRows[^1];

        var currentInsertionIndex = anchorRow is null
            ? remainingRows.Count
            : remainingRows.FindIndex(row => string.Equals(row.FilePath, anchorRow.FilePath, StringComparison.OrdinalIgnoreCase));
        if (currentInsertionIndex < 0)
            currentInsertionIndex = remainingRows.Count;

        foreach (var row in movingRows)
        {
            remainingRows.Insert(currentInsertionIndex, row);
            currentInsertionIndex++;
        }

        _currentTracks = remainingRows;
        IsTrackSortEnabled = false;
        IsTrackOrderDirty = IsPlaylistView;
        _tracks.Clear();
        foreach (var row in _viewTracks)
            _tracks.Add(row);

        SetSelectedTrackIndices(_viewTracks
            .Select((row, index) => (row, index))
            .Where(pair => movingPaths.Contains(pair.row.FilePath))
            .Select(pair => pair.index)
            .ToArray());
    }
}

public enum LibraryNodeType { Folder, File, Playlist }

public sealed class LibraryNode : INotifyPropertyChanged
{
    private string _meta;
    private bool _isExpanded;

    public LibraryNode(string name, string meta, string path,
        IReadOnlyList<LibraryNode>? children = null, bool isExpanded = false,
        LibraryNodeType nodeType = LibraryNodeType.Folder,
        int trackCount = 0)
    {
        Name = name;
        _meta = meta;
        _isExpanded = isExpanded;
        Path = path;
        NodeType = nodeType;
        TrackCount = trackCount;
        Children = children is null
            ? new ObservableCollection<LibraryNode>()
            : new ObservableCollection<LibraryNode>(children);
    }

    public string Name { get; }
    public string Path { get; }
    public LibraryNodeType NodeType { get; }
    public int TrackCount { get; }
    public ObservableCollection<LibraryNode> Children { get; }

    public string Meta
    {
        get => _meta;
        set
        {
            if (_meta == value) return;
            _meta = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Meta)));
        }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TrackRow(
    string FilePath,
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

public sealed record QueueItem(string PrimaryText, string SecondaryText, string Length, bool IsPlaceholder = false);

public readonly record struct TrackView(IReadOnlyList<TrackRow> Tracks, IReadOnlyList<TrackRow> Rows);

public enum SessionMode
{
    Library,
    Radio,
    Stream,
}

public enum QueueDisplayMode
{
    /// <summary>Show metadata Title as primary, Album as secondary.</summary>
    TitleAlbum,
    /// <summary>Show file name (no ext) as primary, parent folder as secondary.</summary>
    FileNameFolder,
    /// <summary>Title+Album when metadata is present, falls back to FileName+Folder.</summary>
    TitleAlbumWithFallback,
    // Legacy aliases kept for config back-compat (mapped on load)
    Title    = TitleAlbum,
    FileName = FileNameFolder,
}

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

/// <summary>
/// Well-known drag payload keys for cross-panel drag-and-drop.
/// </summary>
public static class DragFormats
{
    public const string TrackIndex = "orpheus-track-index";
    public const string TrackIndices = "orpheus-track-indices";
    public const string LibraryNodePath = "orpheus-library-node-path";
    public const string LibraryNodeType = "orpheus-library-node-type";
    public const string DragLabel = "orpheus-drag-label";
}

/// <summary>
/// Lightweight managed drag-and-drop service that replaces the OS-level
/// <see cref="DragDrop.DoDragDropAsync"/> with in-process pointer capture.
/// Drag sources call <see cref="Begin"/>/<see cref="Move"/>/<see cref="End"/>.
/// Drop targets subscribe to the events and do their own hit-testing.
/// </summary>
public sealed class ManagedDragService
{
    public static readonly ManagedDragService Instance = new();

    private ManagedDragService() { }

    /// <summary>Current drag payload (key-value pairs).</summary>
    public Dictionary<string, string>? Payload { get; private set; }

    /// <summary>Latest pointer position in TopLevel client coordinates.</summary>
    public Point ClientPosition { get; private set; }

    /// <summary>The TopLevel that owns the current drag session.</summary>
    public TopLevel? TopLevel { get; private set; }

    /// <summary>Whether a managed drag is currently active.</summary>
    public bool IsDragging { get; private set; }

    public event Action? DragStarted;
    public event Action? DragMoved;
    public event Action? DragEnded;

    public void Begin(TopLevel topLevel, Dictionary<string, string> payload, Point clientPosition)
    {
        TopLevel = topLevel;
        Payload = payload;
        ClientPosition = clientPosition;
        IsDragging = true;
        DragStarted?.Invoke();
    }

    public void Move(Point clientPosition)
    {
        ClientPosition = clientPosition;
        DragMoved?.Invoke();
    }

    public void End()
    {
        IsDragging = false;
        DragEnded?.Invoke();
        Payload = null;
        TopLevel = null;
    }

    /// <summary>
    /// Helper: get the value for a key from the current payload, or null.
    /// </summary>
    public string? GetValue(string key) =>
        Payload is not null && Payload.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Lightweight transparent topmost window used to display a drag preview near the cursor.
/// </summary>
public sealed class DragPreviewWindow : Window
{
    public DragPreviewWindow()
    {
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsEnabled = false;
    }
}

/// <summary>
/// Static helper for showing/moving/hiding the drag preview window.
/// </summary>
public static class DragPreviewService
{
    private static DragPreviewWindow? _window;

    /// <summary>
    /// Show a drag preview at the given screen position.
    /// </summary>
    public static void Show(Control content, TopLevel origin, Point clientPosition, double opacity = 0.75)
    {
        Hide();
        _window = new DragPreviewWindow { Opacity = Math.Clamp(opacity, 0, 1) };

        if (Application.Current!.Resources.TryGetResource(
                "PanelFill", Application.Current.ActualThemeVariant, out var fill)
            && fill is Color fillColor)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(fillColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = content,
                IsHitTestVisible = false,
            };

            if (Application.Current.Resources.TryGetResource(
                    "AccentColor", Application.Current.ActualThemeVariant, out var accent)
                && accent is Color accentColor)
            {
                border.BorderBrush = new SolidColorBrush(accentColor);
                border.BorderThickness = new Thickness(1);
            }

            _window.Content = border;
        }
        else
        {
            _window.Content = content;
        }

        var screenPos = origin.PointToScreen(clientPosition + new Point(16, -8));
        _window.Position = screenPos;
        _window.Show();
    }

    /// <summary>
    /// Move the preview to follow a new position (called from DragOver on drop target).
    /// </summary>
    public static void Move(TopLevel origin, Point clientPosition)
    {
        if (_window is null) return;
        var screenPos = origin.PointToScreen(clientPosition + new Point(16, -8));
        _window.Position = screenPos;
    }

    /// <summary>
    /// Hide and close the preview window.
    /// </summary>
    public static void Hide()
    {
        try { _window?.Close(); }
        catch { /* ignore */ }
        finally { _window = null; }
    }
}

/// <summary>
/// Converts <see cref="LibraryNodeType"/> to an opacity value.
/// Folders are full opacity; files and playlists are slightly dimmed.
/// </summary>
public sealed class NodeTypeToOpacityConverter : IValueConverter
{
    public static readonly NodeTypeToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LibraryNodeType type && type != LibraryNodeType.Folder ? 0.7 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public enum QueueConfirmResult { Save, Discard, Cancel }

/// <summary>
/// A modal dialog asking the user what to do with a dirty play queue
/// before it gets replaced. Returns Save, Discard, or Cancel.
/// </summary>
public sealed class QueueConfirmDialog : Window
{
    private QueueConfirmResult _result = QueueConfirmResult.Cancel;

    public QueueConfirmDialog()
    {
        Title = Lang.Resources.UnsavedQueue;
        Width = 380;
        Height = 100;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.BorderOnly;

        var message = new TextBlock
        {
            Text = Lang.Resources.UnsavedQueueMessage,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(20, 20, 20, 12),
        };

        var saveBtn = new Button { Content = Lang.Resources.Save, Width = 80, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var discardBtn = new Button { Content = Lang.Resources.Discard, Width = 80, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var cancelBtn = new Button { Content = Lang.Resources.Cancel, Width = 80, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        saveBtn.Click += (_, _) => { _result = QueueConfirmResult.Save; Close(); };
        discardBtn.Click += (_, _) => { _result = QueueConfirmResult.Discard; Close(); };
        cancelBtn.Click += (_, _) => { _result = QueueConfirmResult.Cancel; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 0, 20, 16),
        };
        buttons.Children.Add(saveBtn);
        buttons.Children.Add(discardBtn);
        buttons.Children.Add(cancelBtn);

        var root = new StackPanel();
        root.Children.Add(message);
        root.Children.Add(buttons);
        Content = root;
    }

    public new async Task<QueueConfirmResult> ShowDialog(Window owner)
    {
        await base.ShowDialog(owner);
        return _result;
    }
}

using System.Diagnostics;
using Orpheus.Core.Playlist;

namespace Orpheus.Core.Playback;

/// <summary>
/// High-level controller that completely wraps an <see cref="IPlayer"/> and a
/// <see cref="Playlist.Playlist"/>.  The GUI should interact exclusively through
/// this class's async methods and subscribe to its events for state updates.
///
/// Design contract:
/// <list type="bullet">
///   <item>The GUI NEVER reads properties on this class to derive UI state.
///         All UI-relevant data is delivered via event args.</item>
///   <item>Every async method uses <c>ConfigureAwait(false)</c> internally and
///         fires an event when the operation finishes so the GUI can react.</item>
///   <item>The underlying <see cref="IPlayer"/> is fully encapsulated.
///         The GUI never touches it directly.</item>
/// </list>
///
/// Shuffle Play vs Shuffle Playlist:
/// - Shuffle Play: tracks play in a random order, but the playlist's
///   actual item order is unchanged. Managed here via a hidden shuffle sequence.
/// - Shuffle Playlist: physically reorders the playlist items. Use
///   <see cref="Playlist.Playlist.ShuffleItems"/> for that.
/// </summary>
public sealed class PlayerController : IAsyncDisposable
{
    private readonly IPlayer _player;
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private readonly Random _rng = new();
    private readonly List<int> _shuffleOrder = [];
    private bool _shufflePlay;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private bool _disposed;

    // ── Desired state (owned by the controller) ───────────────────────
    private const int SyncIntervalMs = 1;
    private double _desiredVolume = 72;
    private bool _desiredMute;
    private string? _desiredAudioDevice;
    private readonly Timer? _syncTimer;
    private IReadOnlyList<(string? Id, string Description)> _cachedAudioDevices = [];

    // ── Fade configuration ────────────────────────────────────────────
    private const int FadeDurationMs = 300;
    private const int FadeSteps = 15;

    public PlayerController(IPlayer player, string? desiredAudioDevice, double desiredVolume)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _player.MediaEnded += OnMediaEnded;
        _player.StateChanged += OnPlayerStateChanged;
        _player.LoadStateChanged += OnPlayerLoadStateChanged;
        _player.PositionChanged += OnPlayerPositionChanged;
        _player.ErrorOccurred += OnPlayerErrorOccurred;

        RefreshAudioDevices();
        ValidateDesiredAudioDevice(desiredAudioDevice);

        _desiredVolume = Math.Clamp(desiredVolume, 0, 100);
        _player.Volume = (int)Math.Round(_desiredVolume);

        _syncTimer = new Timer(SyncDesiredState, null, SyncIntervalMs, SyncIntervalMs);
    }

    /// <summary>
    /// The active playlist.
    /// </summary>
    public Playlist.Playlist Playlist { get; } = new();

    /// <summary>
    /// Whether to automatically advance to the next track when the current one ends.
    /// </summary>
    public bool AutoAdvance { get; set; } = true;

    /// <summary>
    /// Exposes the underlying <see cref="IPlayer"/> for VLC-specific features
    /// that are not part of the general playback contract (equalizer creation,
    /// audio device enumeration, etc.).  The GUI should NOT use this for
    /// state reads or playback control.
    /// </summary>
    public IPlayer Player => _player;

    // ══════════════════════════════════════════════════════════════════
    //  Events — the sole source of state updates for the GUI
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired whenever the playback state changes (playing, paused, stopped, etc.).
    /// Carries a complete snapshot of playback-relevant state so the GUI never
    /// needs to read back from the controller.
    /// </summary>
    public event EventHandler<PlaybackStateSnapshot>? StateChanged;

    /// <summary>
    /// Fired periodically during playback with the current position / duration.
    /// </summary>
    public event EventHandler<PositionSnapshot>? PositionChanged;

    /// <summary>
    /// Fired when the volume or mute state changes.
    /// </summary>
    public event EventHandler<VolumeSnapshot>? VolumeChanged;

    /// <summary>
    /// Fired when shuffle play mode changes.
    /// </summary>
    public event EventHandler<bool>? ShufflePlayChanged;

    /// <summary>
    /// Fired when repeat mode changes.
    /// </summary>
    public event EventHandler<RepeatMode>? RepeatModeChanged;

    /// <summary>
    /// Fired when a playback error occurs.
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Fired when the list of available audio devices changes.
    /// </summary>
    public event EventHandler<IReadOnlyList<(string? Id, string Description)>>? AudioDevicesChanged;

    // ══════════════════════════════════════════════════════════════════
    //  Repeat
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set the repeat mode and notify subscribers.
    /// </summary>
    public void SetRepeatMode(RepeatMode mode)
    {
        if (_repeatMode == mode) return;
        _repeatMode = mode;
        RepeatModeChanged?.Invoke(this, _repeatMode);
    }

    /// <summary>
    /// Cycle through repeat modes: Off → All → One → Off.
    /// </summary>
    public void CycleRepeatMode()
    {
        SetRepeatMode(_repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  Shuffle Play
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set shuffle play mode and notify subscribers.
    /// </summary>
    public void SetShufflePlay(bool enabled)
    {
        if (_shufflePlay == enabled) return;
        _shufflePlay = enabled;

        if (_shufflePlay)
            RebuildShuffleOrder();
        else
            _shuffleOrder.Clear();

        ShufflePlayChanged?.Invoke(this, _shufflePlay);
    }

    /// <summary>
    /// Toggle shuffle play on/off.
    /// </summary>
    public void ToggleShufflePlay()
    {
        SetShufflePlay(!_shufflePlay);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Volume / Mute
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set the volume level (0–100) and notify subscribers.
    /// </summary>
    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (Math.Abs(_desiredVolume - volume) < 0.01) return;
        _desiredVolume = volume;
        _player.Volume = (int)Math.Round(volume);
        VolumeChanged?.Invoke(this, new VolumeSnapshot(_desiredVolume, _desiredMute));
    }

    /// <summary>
    /// Toggle mute on/off.
    /// </summary>
    public void ToggleMute()
    {
        SetMute(!_desiredMute);
    }

    /// <summary>
    /// Set the mute state.
    /// </summary>
    public void SetMute(bool muted)
    {
        if (_desiredMute == muted) return;
        _desiredMute = muted;
        _player.Volume = muted ? 0 : (int)Math.Round(_desiredVolume);
        VolumeChanged?.Invoke(this, new VolumeSnapshot(_desiredVolume, _desiredMute));
    }

    /// <summary>
    /// Set the audio output device.
    /// </summary>
    public void SetAudioDevice(string? deviceId)
    {
        _desiredAudioDevice = deviceId;
        _player.SetAudioDevice(deviceId);
    }

    /// <summary>
    /// Refresh the cached list of available audio devices.
    /// </summary>
    public void RefreshAudioDevices()
    {
        _cachedAudioDevices = _player.GetAudioOutputDevices();
        AudioDevicesChanged?.Invoke(this, _cachedAudioDevices);
    }

    /// <summary>
    /// Get the cached list of available audio devices.
    /// </summary>
    public IReadOnlyList<(string? Id, string Description)> GetAudioDevices() => _cachedAudioDevices;

    /// <summary>
    /// Check if the desired audio device is still available.
    /// If not, reset to system default.
    /// </summary>
    public void ValidateDesiredAudioDevice(string? desiredAudioDevice)
    {
        _desiredAudioDevice = desiredAudioDevice;
        var availableIds = _cachedAudioDevices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!availableIds.Contains(_desiredAudioDevice))
        {
            SetAudioDevice("");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Seek
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seek to a specific position.  Fires a <see cref="PositionChanged"/>
    /// event with the new position once the seek is applied.
    /// </summary>
    public async Task SeekAsync(TimeSpan position)
    {
        await _player.SeekAsync(position);
        FirePositionSnapshot();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Playback Controls
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Play the track at the specified playlist index.
    /// </summary>
    public async Task PlayAtIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= Playlist.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        Playlist.CurrentIndex = index;

        // If shuffle play is on, rebuild so the newly selected track
        // is at the front of the shuffle sequence.
        if (_shufflePlay)
            RebuildShuffleOrder();

        await FadeOutAsync();
        await _player.PlayAsync(Playlist[index].Source, cancellationToken);
        await FadeInAsync();

        FireStateSnapshot();
    }

    /// <summary>
    /// Play the current track in the playlist.
    /// If no track is selected, starts from the beginning.
    /// </summary>
    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        if (Playlist.Count == 0) return;

        if (Playlist.CurrentIndex < 0)
            Playlist.CurrentIndex = 0;

        if (_shufflePlay && _shuffleOrder.Count == 0)
            RebuildShuffleOrder();

        var item = Playlist.CurrentItem;
        if (item is null) return;

        await FadeOutAsync();
        await _player.PlayAsync(item.Source, cancellationToken);
        await FadeInAsync();

        FireStateSnapshot();
    }

    /// <summary>
    /// Advance to and play the next track, respecting shuffle play and repeat mode.
    /// Uses a lock to prevent concurrent navigation (e.g. user click + auto-advance race).
    /// </summary>
    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        await _navigationLock.WaitAsync(cancellationToken);
        try
        {
            var nextIndex = GetNextIndex();
            if (nextIndex is not null)
            {
                Playlist.CurrentIndex = nextIndex.Value;
                await FadeOutAsync();
                await _player.PlayAsync(Playlist.CurrentItem!.Source, cancellationToken);
                await FadeInAsync();
            }
            else
            {
                // Queue exhausted (repeat off): stop and go back to first track
                await FadeOutAsync();
                await _player.StopAsync();
                RestoreVolume();
                if (Playlist.Count > 0)
                    Playlist.CurrentIndex = 0;
            }
        }
        finally
        {
            _navigationLock.Release();
        }

        FireStateSnapshot();
    }

    /// <summary>
    /// Go back to and play the previous track.
    /// If more than 3 seconds into the current track, restart it instead.
    /// </summary>
    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        await _navigationLock.WaitAsync(cancellationToken);
        try
        {
            // If we're more than 3 seconds in, restart the current track.
            if (_player.PlaybackPosition > TimeSpan.FromSeconds(3))
            {
                await _player.SeekAsync(TimeSpan.Zero);
            }
            else
            {
                var prevIndex = GetPreviousIndex();
                if (prevIndex is not null)
                {
                    Playlist.CurrentIndex = prevIndex.Value;
                    await FadeOutAsync();
                    await _player.PlayAsync(Playlist.CurrentItem!.Source, cancellationToken);
                    await FadeInAsync();
                }
                else
                {
                    await _player.SeekAsync(TimeSpan.Zero);
                }
            }
        }
        finally
        {
            _navigationLock.Release();
        }

        FireStateSnapshot();
    }

    /// <summary>
    /// Pause playback.
    /// </summary>
    public async Task PauseAsync()
    {
        await FadeOutAsync();
        await _player.PauseAsync();
        FireStateSnapshot();
    }

    /// <summary>
    /// Resume playback from a paused state.
    /// </summary>
    public async Task ResumeAsync()
    {
        await _player.ResumeAsync();
        await FadeInAsync();
        FireStateSnapshot();
    }

    /// <summary>
    /// Stop playback.
    /// </summary>
    public async Task StopAsync()
    {
        await FadeOutAsync();
        await _player.StopAsync();
        RestoreVolume();
        FireStateSnapshot();
    }

    /// <summary>
    /// Toggle between play and pause.
    /// </summary>
    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        switch (_player.PlaybackState)
        {
            case PlaybackState.Playing:
                await PauseAsync();
                break;
            case PlaybackState.Paused:
                await ResumeAsync();
                break;
            case PlaybackState.Stopped:
                await PlayAsync(cancellationToken);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  UI Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the upcoming tracks in playback order (respecting shuffle play).
    /// Useful for an "Up Next" panel in the UI.
    /// </summary>
    /// <param name="maxCount">Maximum number of upcoming tracks to return.</param>
    public IReadOnlyList<PlaylistItem> GetUpcomingTracks(int maxCount = 20)
    {
        if (Playlist.Count == 0 || Playlist.CurrentIndex < 0)
            return [];

        var upcoming = new List<PlaylistItem>();

        if (_shufflePlay && _shuffleOrder.Count > 0)
        {
            var currentShufflePos = _shuffleOrder.IndexOf(Playlist.CurrentIndex);
            for (var i = currentShufflePos + 1; i < _shuffleOrder.Count && upcoming.Count < maxCount; i++)
            {
                upcoming.Add(Playlist[_shuffleOrder[i]]);
            }
        }
        else
        {
            for (var i = Playlist.CurrentIndex + 1; i < Playlist.Count && upcoming.Count < maxCount; i++)
            {
                upcoming.Add(Playlist[i]);
            }

            // If repeat all is on, wrap around.
            if (_repeatMode == RepeatMode.All && upcoming.Count < maxCount)
            {
                for (var i = 0; i < Playlist.CurrentIndex && upcoming.Count < maxCount; i++)
                {
                    upcoming.Add(Playlist[i]);
                }
            }
        }

        return upcoming;
    }

    /// <summary>
    /// Get the current position within the playback order.
    /// Returns (current, total) for a "Track 3 of 12" UI display.
    /// In shuffle play mode, this reflects position in the shuffle sequence.
    /// </summary>
    public (int Current, int Total) GetPlaybackPosition()
    {
        if (Playlist.Count == 0 || Playlist.CurrentIndex < 0)
            return (0, Playlist.Count);

        if (_shufflePlay && _shuffleOrder.Count > 0)
        {
            var pos = _shuffleOrder.IndexOf(Playlist.CurrentIndex);
            return (pos + 1, _shuffleOrder.Count);
        }

        return (Playlist.CurrentIndex + 1, Playlist.Count);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private — Fade helpers
    // ══════════════════════════════════════════════════════════════════

    private async Task FadeOutAsync()
    {
        // If muted, nothing to fade — audio is already silent.
        if (_desiredMute) return;

        var startVolume = _player.Volume;
        if (startVolume <= 0) return;
        var stepDelay = FadeDurationMs / FadeSteps;
        for (var i = FadeSteps - 1; i >= 0; i--)
        {
            _player.Volume = (int)(startVolume * i / (double)FadeSteps);
            await Task.Delay(stepDelay);
        }
        _player.Volume = 0;
    }

    private async Task FadeInAsync()
    {
        var targetVolume = (int)Math.Round(_desiredVolume);

        // If muted, restore the volume level silently and ensure mute
        // stays applied.
        if (_desiredMute)
        {
            _player.Volume = 0;
            return;
        }

        if (targetVolume <= 0) return;
        _player.Volume = 0;
        var stepDelay = FadeDurationMs / FadeSteps;
        for (var i = 1; i <= FadeSteps; i++)
        {
            _player.Volume = (int)(targetVolume * i / (double)FadeSteps);
            await Task.Delay(stepDelay);
        }
        _player.Volume = targetVolume;
    }

    /// <summary>
    /// Restore the volume level (so the next play starts at
    /// the user's chosen volume).
    /// </summary>
    private void RestoreVolume()
    {
        _player.Volume = (int)Math.Round(_desiredVolume);
    }

    private void SyncDesiredState(object? state)
    {
        if (_disposed) return;

        var currentVolume = _player.Volume;
        var desiredVolumeInt = _desiredMute ? 0 : (int)Math.Round(_desiredVolume);
        var currentDevice = _player.GetCurrentAudioDevice();

        if (currentDevice != _desiredAudioDevice)
        {
            _player.Volume = 0;
            _player.SetAudioDevice(_desiredAudioDevice);
            if (_player.GetCurrentAudioDevice() == _desiredAudioDevice)
            {
                _player.Volume = desiredVolumeInt;
            }
        }
        else if (currentVolume != desiredVolumeInt)
        {
            _player.Volume = desiredVolumeInt;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private — Snapshot builders & event firing
    // ══════════════════════════════════════════════════════════════════

    private void FireStateSnapshot()
    {
        var handler = StateChanged;
        if (handler is null) return;

        var state = _player.PlaybackState;
        var loadState = _player.LoadState;
        var position = state == PlaybackState.Stopped ? TimeSpan.Zero : _player.PlaybackPosition;
        var duration = _player.MediaDuration ?? TimeSpan.Zero;
        var currentItem = Playlist.CurrentItem;
        var currentIndex = Playlist.CurrentIndex;

        var snapshot = new PlaybackStateSnapshot(
            state, loadState, position, duration, currentItem, currentIndex, Playlist.Count);

        handler.Invoke(this, snapshot);
    }

    private void FirePositionSnapshot()
    {
        var handler = PositionChanged;
        if (handler is null) return;

        var position = _player.PlaybackPosition;
        var duration = _player.MediaDuration ?? TimeSpan.Zero;

        handler.Invoke(this, new PositionSnapshot(position, duration));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private — IPlayer event handlers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Relay IPlayer state changes as controller-level state snapshots.
    /// These fire from background threads (VlcPlayer uses ThreadPool), so
    /// the GUI must marshal to its UI thread in its own handler.
    /// </summary>
    private void OnPlayerStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        FireStateSnapshot();
    }

    /// <summary>
    /// Relay IPlayer load-state changes as controller-level state snapshots.
    /// </summary>
    private void OnPlayerLoadStateChanged(object? sender, LoadStateChangedEventArgs e)
    {
        FireStateSnapshot();
    }

    /// <summary>
    /// Relay IPlayer position changes as controller-level position snapshots.
    /// </summary>
    private void OnPlayerPositionChanged(object? sender, TimeSpan position)
    {
        var handler = PositionChanged;
        if (handler is null) return;

        var duration = _player.MediaDuration ?? TimeSpan.Zero;
        handler.Invoke(this, new PositionSnapshot(position, duration));
    }

    private void OnPlayerErrorOccurred(object? sender, string message)
    {
        ErrorOccurred?.Invoke(this, message);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private — Navigation helpers
    // ══════════════════════════════════════════════════════════════════

    private int? GetNextIndex()
    {
        if (Playlist.Count == 0) return null;

        // Repeat One: stay on the same track.
        if (_repeatMode == RepeatMode.One)
            return Playlist.CurrentIndex;

        if (_shufflePlay && _shuffleOrder.Count > 0)
        {
            var currentShufflePos = _shuffleOrder.IndexOf(Playlist.CurrentIndex);
            var nextShufflePos = currentShufflePos + 1;

            if (nextShufflePos >= _shuffleOrder.Count)
            {
                if (_repeatMode == RepeatMode.All)
                {
                    RebuildShuffleOrder();
                    return _shuffleOrder.Count > 0 ? _shuffleOrder[0] : null;
                }
                return null; // Shuffle exhausted, repeat off.
            }

            return _shuffleOrder[nextShufflePos];
        }

        // Sequential.
        var next = Playlist.CurrentIndex + 1;
        if (next >= Playlist.Count)
        {
            return _repeatMode == RepeatMode.All ? 0 : null;
        }

        return next;
    }

    private int? GetPreviousIndex()
    {
        if (Playlist.Count == 0) return null;

        // Repeat One: stay on the same track.
        if (_repeatMode == RepeatMode.One)
            return Playlist.CurrentIndex;

        if (_shufflePlay && _shuffleOrder.Count > 0)
        {
            var currentShufflePos = _shuffleOrder.IndexOf(Playlist.CurrentIndex);
            var prevShufflePos = currentShufflePos - 1;

            if (prevShufflePos < 0)
            {
                return _repeatMode == RepeatMode.All ? _shuffleOrder[^1] : null;
            }

            return _shuffleOrder[prevShufflePos];
        }

        // Sequential.
        var prev = Playlist.CurrentIndex - 1;
        if (prev < 0)
        {
            return _repeatMode == RepeatMode.All ? Playlist.Count - 1 : null;
        }

        return prev;
    }

    private void RebuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        for (var i = 0; i < Playlist.Count; i++)
            _shuffleOrder.Add(i);

        // Fisher-Yates shuffle.
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        // Move current track to front so it doesn't replay immediately.
        if (Playlist.CurrentIndex >= 0 && Playlist.CurrentIndex < Playlist.Count)
        {
            _shuffleOrder.Remove(Playlist.CurrentIndex);
            _shuffleOrder.Insert(0, Playlist.CurrentIndex);
        }
    }

    private async void OnMediaEnded(object? sender, EventArgs e)
    {
        if (!AutoAdvance) return;

        try
        {
            await NextAsync();
        }
        catch
        {
            // Swallow exceptions from auto-advance to avoid unobserved task exceptions.
            // Errors will be reported via Player.ErrorOccurred.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _syncTimer?.Dispose();
        _player.MediaEnded -= OnMediaEnded;
        _player.StateChanged -= OnPlayerStateChanged;
        _player.LoadStateChanged -= OnPlayerLoadStateChanged;
        _player.PositionChanged -= OnPlayerPositionChanged;
        _player.ErrorOccurred -= OnPlayerErrorOccurred;
        await _player.DisposeAsync();
        _navigationLock.Dispose();
    }
}

// ══════════════════════════════════════════════════════════════════════
//  Snapshot records — immutable data bags delivered via events
// ══════════════════════════════════════════════════════════════════════

/// <summary>
/// Complete snapshot of playback state, delivered via
/// <see cref="PlayerController.StateChanged"/>.
/// </summary>
public sealed record PlaybackStateSnapshot(
    PlaybackState State,
    LoadState LoadState,
    TimeSpan Position,
    TimeSpan Duration,
    PlaylistItem? CurrentItem,
    int CurrentIndex,
    int PlaylistCount)
{
    public bool IsPlaying => State == PlaybackState.Playing;
    public bool IsStopped => State == PlaybackState.Stopped;
}

/// <summary>
/// Position/duration snapshot delivered via
/// <see cref="PlayerController.PositionChanged"/>.
/// </summary>
public sealed record PositionSnapshot(
    TimeSpan Position,
    TimeSpan Duration);

/// <summary>
/// Volume/mute snapshot delivered via
/// <see cref="PlayerController.VolumeChanged"/>.
/// </summary>
public sealed record VolumeSnapshot(
    double Volume,
    bool IsMuted);

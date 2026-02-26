using Orpheus.Core.Playlist;

namespace Orpheus.Core.Playback;

/// <summary>
/// High-level controller that connects an IPlayer with a Playlist.
/// Owns shuffle play state, repeat mode, and track navigation.
///
/// Shuffle Play vs Shuffle Playlist:
/// - Shuffle Play: tracks play in a random order, but the playlist's
///   actual item order is unchanged. Managed here via a hidden shuffle sequence.
/// - Shuffle Playlist: physically reorders the playlist items. Use
///   <see cref="Playlist.Playlist.ShuffleItems"/> for that.
/// </summary>
public sealed class PlayerController : IDisposable
{
    private readonly IPlayer _player;
    private readonly Random _rng = new();
    private readonly List<int> _shuffleOrder = [];
    private bool _shufflePlay;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private bool _disposed;

    public PlayerController(IPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _player.MediaEnded += OnMediaEnded;
    }

    /// <summary>
    /// The underlying player instance.
    /// </summary>
    public IPlayer Player => _player;

    /// <summary>
    /// The active playlist.
    /// </summary>
    public Playlist.Playlist Playlist { get; } = new();

    /// <summary>
    /// Whether to automatically advance to the next track when the current one ends.
    /// </summary>
    public bool AutoAdvance { get; set; } = true;

    // ── Repeat ────────────────────────────────────────────────────────

    /// <summary>
    /// Current repeat mode: Off, One, or All (playlist).
    /// </summary>
    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (_repeatMode == value) return;
            _repeatMode = value;
            RepeatModeChanged?.Invoke(this, _repeatMode);
        }
    }

    /// <summary>
    /// Cycle through repeat modes: Off → All → One → Off.
    /// Returns the new mode.
    /// </summary>
    public RepeatMode CycleRepeatMode()
    {
        RepeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
        return _repeatMode;
    }

    /// <summary>
    /// Fired when RepeatMode changes.
    /// </summary>
    public event EventHandler<RepeatMode>? RepeatModeChanged;

    // ── Shuffle Play ──────────────────────────────────────────────────

    /// <summary>
    /// Whether shuffle play is active. When enabled, Next/Previous follow
    /// a random sequence instead of playlist order. The playlist itself
    /// is not reordered.
    /// </summary>
    public bool ShufflePlay
    {
        get => _shufflePlay;
        set
        {
            if (_shufflePlay == value) return;
            _shufflePlay = value;

            if (_shufflePlay)
                RebuildShuffleOrder();
            else
                _shuffleOrder.Clear();

            ShufflePlayChanged?.Invoke(this, _shufflePlay);
        }
    }

    /// <summary>
    /// Toggle shuffle play on/off. Returns the new state.
    /// </summary>
    public bool ToggleShufflePlay()
    {
        ShufflePlay = !ShufflePlay;
        return ShufflePlay;
    }

    /// <summary>
    /// Fired when ShufflePlay changes.
    /// </summary>
    public event EventHandler<bool>? ShufflePlayChanged;

    // ── Playback Controls ─────────────────────────────────────────────

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

        await _player.PlayAsync(Playlist[index].Source, cancellationToken).ConfigureAwait(false);
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

        await _player.PlayAsync(item.Source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advance to and play the next track, respecting shuffle play and repeat mode.
    /// </summary>
    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        var nextIndex = GetNextIndex();
        if (nextIndex is not null)
        {
            Playlist.CurrentIndex = nextIndex.Value;
            await _player.PlayAsync(Playlist.CurrentItem!.Source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _player.Stop();
        }
    }

    /// <summary>
    /// Go back to and play the previous track.
    /// If more than 3 seconds into the current track, restart it instead.
    /// </summary>
    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        // If we're more than 3 seconds in, restart the current track.
        if (_player.Position > TimeSpan.FromSeconds(3))
        {
            _player.Seek(TimeSpan.Zero);
            return;
        }

        var prevIndex = GetPreviousIndex();
        if (prevIndex is not null)
        {
            Playlist.CurrentIndex = prevIndex.Value;
            await _player.PlayAsync(Playlist.CurrentItem!.Source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _player.Seek(TimeSpan.Zero);
        }
    }

    public void Pause() => _player.Pause();
    public void Resume() => _player.Resume();
    public void Stop() => _player.Stop();

    /// <summary>
    /// Toggle between play and pause.
    /// </summary>
    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        switch (_player.State)
        {
            case PlaybackState.Playing:
                _player.Pause();
                break;
            case PlaybackState.Paused:
                _player.Resume();
                break;
            case PlaybackState.Stopped:
                await PlayAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    // ── UI Helpers ────────────────────────────────────────────────────

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

    // ── Private ───────────────────────────────────────────────────────

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
            await NextAsync().ConfigureAwait(false);
        }
        catch
        {
            // Swallow exceptions from auto-advance to avoid unobserved task exceptions.
            // Errors will be reported via Player.ErrorOccurred.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _player.MediaEnded -= OnMediaEnded;
        _player.Dispose();
    }
}

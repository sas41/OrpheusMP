using Orpheus.Core.Playlist;

namespace Orpheus.Core.Playback;

/// <summary>
/// High-level controller that connects an IPlayer with a Playlist,
/// handling track advancement, repeat, shuffle, and queue management.
/// </summary>
public sealed class PlayerController : IDisposable
{
    private readonly IPlayer _player;
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

    /// <summary>
    /// Play the track at the specified playlist index.
    /// </summary>
    public async Task PlayAtIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= Playlist.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        Playlist.CurrentIndex = index;
        var item = Playlist[index];
        await _player.PlayAsync(item.Source, cancellationToken).ConfigureAwait(false);
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

        var item = Playlist.CurrentItem;
        if (item is null) return;

        await _player.PlayAsync(item.Source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advance to and play the next track.
    /// </summary>
    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        var next = Playlist.MoveNext();
        if (next is not null)
            await _player.PlayAsync(next.Source, cancellationToken).ConfigureAwait(false);
        else
            _player.Stop();
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

        var prev = Playlist.MovePrevious();
        if (prev is not null)
            await _player.PlayAsync(prev.Source, cancellationToken).ConfigureAwait(false);
        else
            _player.Seek(TimeSpan.Zero); // At the start of the playlist, restart current.
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

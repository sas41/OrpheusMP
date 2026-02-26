using LibVLCSharp.Shared;
using Orpheus.Core.Media;

namespace Orpheus.Core.Playback;

/// <summary>
/// IPlayer implementation backed by LibVLC.
/// Supports local files, network streams, and internet radio.
/// </summary>
public sealed class VlcPlayer : IPlayer
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private PlaybackState _state = PlaybackState.Stopped;
    private MediaSource? _currentSource;
    private bool _disposed;

    public VlcPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(enableDebugLogs: false);
        _mediaPlayer = new MediaPlayer(_libVlc);

        AttachEvents();
    }

    /// <summary>
    /// Create a VLC player with custom LibVLC arguments.
    /// Useful for passing options like --no-video, --network-caching, etc.
    /// </summary>
    public VlcPlayer(params string[] libVlcArgs)
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(libVlcArgs);
        _mediaPlayer = new MediaPlayer(_libVlc);

        AttachEvents();
    }

    public PlaybackState State => _state;

    public TimeSpan Position
    {
        get
        {
            if (_state is PlaybackState.Stopped or PlaybackState.Error)
                return TimeSpan.Zero;

            return TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            var length = _mediaPlayer.Length;
            return length <= 0 ? null : TimeSpan.FromMilliseconds(length);
        }
    }

    public int Volume
    {
        get => _mediaPlayer.Volume;
        set => _mediaPlayer.Volume = Math.Clamp(value, 0, 100);
    }

    public bool IsMuted
    {
        get => _mediaPlayer.Mute;
        set => _mediaPlayer.Mute = value;
    }

    public MediaSource? CurrentSource => _currentSource;

    public async Task PlayAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        Stop();

        _currentSource = source;

        using var media = new LibVLCSharp.Shared.Media(_libVlc, source.Uri);

        // For network streams, add buffering options.
        if (source.Type != MediaSourceType.LocalFile)
        {
            media.AddOption(":network-caching=3000");
        }

        var tcs = new TaskCompletionSource<bool>();

        void OnPlaying(object? sender, EventArgs e)
        {
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.EncounteredError -= OnError;
            tcs.TrySetResult(true);
        }

        void OnError(object? sender, EventArgs e)
        {
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.EncounteredError -= OnError;
            tcs.TrySetException(new InvalidOperationException(
                $"Failed to play media: {source.Uri}"));
        }

        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.EncounteredError += OnError;

        _mediaPlayer.Play(media);

        // Wait for playback to start or fail, with cancellation support.
        await using (cancellationToken.Register(() =>
        {
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.EncounteredError -= OnError;
            _mediaPlayer.Stop();
            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            await tcs.Task.ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Playing)
            _mediaPlayer.Pause();
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Paused)
            _mediaPlayer.Pause(); // VLC toggles pause.
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != PlaybackState.Stopped)
        {
            _mediaPlayer.Stop();
            _currentSource = null;
        }
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state is PlaybackState.Playing or PlaybackState.Paused)
        {
            _mediaPlayer.Time = (long)position.TotalMilliseconds;
        }
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler<string>? ErrorOccurred;

    private void AttachEvents()
    {
        _mediaPlayer.Playing += (_, _) => SetState(PlaybackState.Playing);
        _mediaPlayer.Paused += (_, _) => SetState(PlaybackState.Paused);
        _mediaPlayer.Stopped += (_, _) => SetState(PlaybackState.Stopped);
        _mediaPlayer.Opening += (_, _) => SetState(PlaybackState.Opening);
        _mediaPlayer.Buffering += (_, e) =>
        {
            // LibVLC fires Buffering with 100% when buffering is complete.
            if (e.Cache < 100f)
                SetState(PlaybackState.Buffering);
        };

        _mediaPlayer.EndReached += (_, _) =>
        {
            // EndReached fires from VLC's event thread. We must not call
            // Stop() directly here (deadlock). Post to thread pool.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetState(PlaybackState.Stopped);
                _currentSource = null;
                MediaEnded?.Invoke(this, EventArgs.Empty);
            });
        };

        _mediaPlayer.EncounteredError += (_, _) =>
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, $"Playback error on: {_currentSource?.Uri}");
        };

        _mediaPlayer.TimeChanged += (_, e) =>
        {
            PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Time));
        };
    }

    private void SetState(PlaybackState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;

        _state = newState;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(oldState, newState));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}

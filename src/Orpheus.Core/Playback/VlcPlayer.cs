using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using Orpheus.Core.Effects;
using Orpheus.Core.Media;

namespace Orpheus.Core.Playback;

/// <summary>
/// IPlayer implementation backed by LibVLC.
/// Supports local files, network streams, and internet radio.
/// </summary>
public sealed class VlcPlayer : IPlayer
{
    private volatile bool _stoppingIntentionally;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private PlaybackState _state = PlaybackState.Stopped;
    private LoadState _loadState = LoadState.Complete;
    private MediaSource? _currentSource;
    private int targetVolume = 50;
    private bool _disposed;
    private readonly Timer? _positionTimer;
    private TimeSpan _latestPosition;
    private readonly Channel<Action> _eventChannel;
    private readonly CancellationTokenSource _eventCts;
    public string? GetCurrentAudioDevice() => _mediaPlayer.OutputDevice;

    /// <summary>
    /// Create a VLC player with custom LibVLC arguments.
    /// Useful for passing options like --no-video, --network-caching, etc.
    /// </summary>
    public VlcPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video", "--network-caching=3000");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _positionTimer = new Timer(OnPositionTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        _eventChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        _eventCts = new CancellationTokenSource();
        _ = Task.Run(() => ProcessEventsAsync(_eventCts.Token));

        AttachEvents();
    }

    public PlaybackState PlaybackState => _state;
    public LoadState LoadState => _loadState;

    public TimeSpan PlaybackPosition
    {
        get
        {
            if (_state == PlaybackState.Stopped)
                return TimeSpan.Zero;

            return TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        }
    }

    public TimeSpan? MediaDuration
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
        set
        {
            targetVolume = Math.Clamp(value, 0, 100);
            _mediaPlayer.Volume = targetVolume;
        }
    }

    public MediaSource? CurrentSource => _currentSource;


    public async Task PlayAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        await _playbackLock.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();

            _currentSource = source;

            using var media = new LibVLCSharp.Shared.Media(_libVlc, source.Uri);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
                // Run Stop on thread pool to avoid deadlock from cancellation context.
                _stoppingIntentionally = true;
                ThreadPool.QueueUserWorkItem(_ => _mediaPlayer.Stop());
                tcs.TrySetCanceled(cancellationToken);
            }))
            {
                await tcs.Task;
            }

        }
        catch
        {
            throw;
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    public Task PauseAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Playing)
        {
            return Task.Run(() =>
            {
                _mediaPlayer.Pause();
            });
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Paused)
        {
            return Task.Run(() =>
            {
                _mediaPlayer.Pause(); // VLC toggles pause.
            });
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _playbackLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    /// <summary>
    /// Stop playback on a thread pool thread to avoid deadlocking with
    /// LibVLC's internal event dispatch thread. Must be called while
    /// holding <see cref="_playbackLock"/>.
    /// </summary>
    private Task StopInternalAsync()
    {
        if (_state == PlaybackState.Stopped)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            _stoppingIntentionally = true;
            _mediaPlayer.Stop();
            _currentSource = null;
        });
    }

    public Task SeekAsync(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Stopped)
        {
            return Task.CompletedTask;
        }

        _mediaPlayer.SeekTo(position);
        return Task.CompletedTask;
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<LoadStateChangedEventArgs>? LoadStateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler<string>? ErrorOccurred;

    // ── Equalizer ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="VlcEqualizer"/> bound to this player's internal MediaPlayer.
    /// The caller is responsible for disposing the returned equalizer.
    /// </summary>
    public VlcEqualizer CreateEqualizer() => new(_mediaPlayer);

    // ── Audio output device enumeration / selection ──────────────────

    /// <summary>
    /// Returns the available audio output devices.
    /// Each entry is (id, description) where id can be passed to <see cref="SetAudioDevice"/>.
    /// The first entry is always ("", "System Default").
    /// </summary>
    public IReadOnlyList<(string? Id, string Description)> GetAudioOutputDevices()
    {
        var devices = new List<(string?, string)> { (null, "System Default") };
        var enumerated = _mediaPlayer.AudioOutputDeviceEnum;
        foreach (var dev in enumerated)
        {
            devices.Add((dev.DeviceIdentifier, dev.Description));
        }
        return devices;
    }

    /// <summary>
    /// Selects an audio output device by its identifier.
    /// Pass null to use the system default (resets VLC's explicit device selection).
    /// Returns true if the device was set successfully.
    /// </summary>
    /// <remarks>
    /// libvlc_audio_output_device_set requires an active audio output.
    /// This call may have no effect if nothing is playing or the audio
    /// pipeline hasn't finished initializing yet.
    /// </remarks>
    public bool SetAudioDevice(string? deviceId)
    {
        _mediaPlayer.SetOutputDevice(deviceId);
        return true;
    }

    // private async void OnAudioDeviceChanged(object? sender, MediaPlayerAudioDeviceEventArgs e)
    // {
    //     Console.WriteLine($"AUDIO DEVICE CHANGED TO: {e.AudioDevice}");
    // }

    private void AttachEvents()
    {
        // Broken in libVLC 3.x
        //_mediaPlayer.AudioDevice += OnAudioDeviceChanged;
        _mediaPlayer.Playing += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetPlaybackState(PlaybackState.Playing);
                _positionTimer?.Change(0, 100);
            });
        };
        _mediaPlayer.Paused += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetPlaybackState(PlaybackState.Paused);
                _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            });
        };
        _mediaPlayer.Stopped += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var naturalEnd = !_stoppingIntentionally;
                _stoppingIntentionally = false;
                SetPlaybackState(PlaybackState.Stopped);
                SetLoadState(LoadState.Complete);
                _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                if (naturalEnd)
                {
                    _currentSource = null;
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                }
            });
        };
        _mediaPlayer.Opening += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ => SetLoadState(LoadState.Opening));
        };
        _mediaPlayer.Buffering += (_, e) =>
        {
            if (e.Cache < 100f)
                ThreadPool.QueueUserWorkItem(_ => SetLoadState(LoadState.Buffering));
        };

        _mediaPlayer.EncounteredError += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetLoadState(LoadState.Error);
                SetPlaybackState(PlaybackState.Stopped);
                ErrorOccurred?.Invoke(this, $"Playback error on: {_currentSource?.Uri}");
            });
        };

        _mediaPlayer.TimeChanged += (_, e) =>
        {
            _latestPosition = TimeSpan.FromMilliseconds(e.Time);
        };
    }

    private void OnPositionTimerTick(object? state)
    {
        var handler = PositionChanged;
        if (handler is not null)
        {
            var position = _latestPosition;
            _eventChannel.Writer.TryWrite(() =>
            {
                handler.Invoke(this, position);
            });
        }
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var action = await _eventChannel.Reader.ReadAsync(ct);
                action();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetPlaybackState(PlaybackState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;

        _state = newState;

        var handler = StateChanged;
        if (handler is not null)
        {
            var args = new PlaybackStateChangedEventArgs(oldState, newState);
            _eventChannel.Writer.TryWrite(() =>
            {
                handler.Invoke(this, args);
            });
        }
    }

    private void SetLoadState(LoadState newState)
    {
        var oldState = _loadState;
        if (oldState == newState) return;

        _loadState = newState;

        var handler = LoadStateChanged;
        if (handler is not null)
        {
            var args = new LoadStateChangedEventArgs(oldState, newState);
            _eventChannel.Writer.TryWrite(() =>
            {
                handler.Invoke(this, args);
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer?.Dispose();
        _eventCts.Cancel();
        _eventChannel.Writer.Complete();
        _stoppingIntentionally = true;
        await Task.Run(() => _mediaPlayer.Stop());
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _playbackLock.Dispose();
        _eventCts.Dispose();
    }
}

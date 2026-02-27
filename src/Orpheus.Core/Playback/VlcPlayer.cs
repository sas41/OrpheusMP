using System.Diagnostics;
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
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private PlaybackState _state = PlaybackState.Stopped;
    private LoadState _loadState = LoadState.Complete;
    private MediaSource? _currentSource;
    private bool _isMuted;
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
    public LoadState LoadState => _loadState;

    public TimeSpan Position
    {
        get
        {
            if (_state == PlaybackState.Stopped)
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
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (value)
                _mediaPlayer.SetAudioTrack(-1);
            else
                _mediaPlayer.SetAudioTrack(0);
        }
    }

    public MediaSource? CurrentSource => _currentSource;

    public async Task PlayAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        Debug.WriteLine($"[VlcPlayer.PlayAsync] Called — uri={source?.Uri}, state={_state}, thread={Environment.CurrentManagedThreadId}");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        Debug.WriteLine("[VlcPlayer.PlayAsync] Waiting for _playbackLock...");
        await _playbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Debug.WriteLine("[VlcPlayer.PlayAsync] Acquired _playbackLock");
        try
        {
            // Capture mute/volume state before stopping — LibVLC resets
            // these when new media starts.  Use our own _isMuted backing
            // field rather than _mediaPlayer.Mute, which LibVLC may have
            // already cleared when the previous media ended.
            var savedMute = _isMuted;
            var savedVolume = _mediaPlayer.Volume;

            Debug.WriteLine("[VlcPlayer.PlayAsync] Calling StopInternalAsync...");
            await StopInternalAsync().ConfigureAwait(false);
            Debug.WriteLine($"[VlcPlayer.PlayAsync] StopInternalAsync completed, state={_state}");

            _currentSource = source;

            using var media = new LibVLCSharp.Shared.Media(_libVlc, source.Uri);

            // For network streams, add buffering options.
            if (source.Type != MediaSourceType.LocalFile)
            {
                media.AddOption(":network-caching=3000");
                Debug.WriteLine("[VlcPlayer.PlayAsync] Added network-caching option");
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPlaying(object? sender, EventArgs e)
            {
                Debug.WriteLine($"[VlcPlayer.PlayAsync] OnPlaying fired, thread={Environment.CurrentManagedThreadId}");
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.EncounteredError -= OnError;
                tcs.TrySetResult(true);
            }

            void OnError(object? sender, EventArgs e)
            {
                Debug.WriteLine($"[VlcPlayer.PlayAsync] OnError fired, thread={Environment.CurrentManagedThreadId}");
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.EncounteredError -= OnError;
                tcs.TrySetException(new InvalidOperationException(
                    $"Failed to play media: {source.Uri}"));
            }

            _mediaPlayer.Playing += OnPlaying;
            _mediaPlayer.EncounteredError += OnError;

            Debug.WriteLine($"[VlcPlayer.PlayAsync] Calling _mediaPlayer.Play — uri={source.Uri}");
            await Task.Run(() => {
                    _mediaPlayer.Play(media);
                });
            Debug.WriteLine("[VlcPlayer.PlayAsync] _mediaPlayer.Play returned");

            // Re-apply mute state after playback starts (LibVLC resets it).
            if (savedMute)
                _mediaPlayer.SetAudioTrack(-1);

            // Wait for playback to start or fail, with cancellation support.
            await using (cancellationToken.Register(() =>
            {
                Debug.WriteLine("[VlcPlayer.PlayAsync] CancellationToken triggered — stopping player");
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.EncounteredError -= OnError;
                // Run Stop on thread pool to avoid deadlock from cancellation context.
                ThreadPool.QueueUserWorkItem(_ => _mediaPlayer.Stop());
                tcs.TrySetCanceled(cancellationToken);
            }))
            {
                Debug.WriteLine("[VlcPlayer.PlayAsync] Awaiting TCS for Playing/Error...");
                await tcs.Task.ConfigureAwait(false);
            }

            Debug.WriteLine($"[VlcPlayer.PlayAsync] Playback started successfully, state={_state}, mute={savedMute}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VlcPlayer.PlayAsync] Exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            _playbackLock.Release();
            Debug.WriteLine("[VlcPlayer.PlayAsync] Released _playbackLock");
        }
    }

    public Task PauseAsync()
    {
        Debug.WriteLine($"[VlcPlayer.PauseAsync] Called — state={_state}, thread={Environment.CurrentManagedThreadId}");
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Playing)
        {
            Debug.WriteLine("[VlcPlayer.PauseAsync] State is Playing, dispatching _mediaPlayer.Pause to thread pool");
            return Task.Run(() =>
            {
                Debug.WriteLine($"[VlcPlayer.PauseAsync] Executing _mediaPlayer.Pause on thread={Environment.CurrentManagedThreadId}");
                _mediaPlayer.Pause();
                Debug.WriteLine($"[VlcPlayer.PauseAsync] _mediaPlayer.Pause completed, state={_state}");
            });
        }

        Debug.WriteLine($"[VlcPlayer.PauseAsync] Skipped — state is {_state}, not Playing");
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        Debug.WriteLine($"[VlcPlayer.ResumeAsync] Called — state={_state}, thread={Environment.CurrentManagedThreadId}");
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Paused)
        {
            Debug.WriteLine("[VlcPlayer.ResumeAsync] State is Paused, dispatching _mediaPlayer.Pause (toggle) to thread pool");
            return Task.Run(() =>
            {
                Debug.WriteLine($"[VlcPlayer.ResumeAsync] Executing _mediaPlayer.Pause (toggle) on thread={Environment.CurrentManagedThreadId}");
                _mediaPlayer.Pause(); // VLC toggles pause.
                Debug.WriteLine($"[VlcPlayer.ResumeAsync] _mediaPlayer.Pause (toggle) completed, state={_state}");
            });
        }

        Debug.WriteLine($"[VlcPlayer.ResumeAsync] Skipped — state is {_state}, not Paused");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Debug.WriteLine($"[VlcPlayer.StopAsync] Called — state={_state}, thread={Environment.CurrentManagedThreadId}");
        ObjectDisposedException.ThrowIf(_disposed, this);

        Debug.WriteLine("[VlcPlayer.StopAsync] Waiting for _playbackLock...");
        await _playbackLock.WaitAsync().ConfigureAwait(false);
        Debug.WriteLine("[VlcPlayer.StopAsync] Acquired _playbackLock");
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            Debug.WriteLine($"[VlcPlayer.StopAsync] StopInternalAsync completed, state={_state}");
        }
        finally
        {
            _playbackLock.Release();
            Debug.WriteLine("[VlcPlayer.StopAsync] Released _playbackLock");
        }
    }

    /// <summary>
    /// Stop playback on a thread pool thread to avoid deadlocking with
    /// LibVLC's internal event dispatch thread. Must be called while
    /// holding <see cref="_playbackLock"/>.
    /// </summary>
    private Task StopInternalAsync()
    {
        Debug.WriteLine($"[VlcPlayer.StopInternalAsync] Called — state={_state}, thread={Environment.CurrentManagedThreadId}");

        if (_state == PlaybackState.Stopped)
        {
            Debug.WriteLine("[VlcPlayer.StopInternalAsync] Already stopped, returning");
            return Task.CompletedTask;
        }

        Debug.WriteLine("[VlcPlayer.StopInternalAsync] Dispatching _mediaPlayer.Stop to thread pool");
        return Task.Run(() =>
        {
            Debug.WriteLine($"[VlcPlayer.StopInternalAsync] Executing _mediaPlayer.Stop on thread={Environment.CurrentManagedThreadId}");
            _mediaPlayer.Stop();
            _currentSource = null;
            Debug.WriteLine($"[VlcPlayer.StopInternalAsync] _mediaPlayer.Stop completed, state={_state}");
        });
    }

    public Task SeekAsync(TimeSpan position)
    {
        Debug.WriteLine($"[VlcPlayer.SeekAsync] Called — position={position}, state={_state}, thread={Environment.CurrentManagedThreadId}");
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == PlaybackState.Stopped)
        {
            Debug.WriteLine($"[VlcPlayer.SeekAsync] Skipped — state is {_state}");
            return Task.CompletedTask;
        }

        Debug.WriteLine($"[VlcPlayer.SeekAsync] Seeking to {position.TotalMilliseconds}ms");
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
    public IReadOnlyList<(string Id, string Description)> GetAudioOutputDevices()
    {
        var devices = new List<(string, string)> { ("", "System Default") };

        // LibVLCSharp AudioOutputDevices needs an output module name.
        // Enumerate the default output module's devices.
        foreach (var output in _libVlc.AudioOutputs)
        {
            foreach (var dev in _libVlc.AudioOutputDevices(output.Name))
            {
                devices.Add((dev.DeviceIdentifier, $"{dev.Description} ({output.Name})"));
            }
            break; // Only enumerate the primary output module
        }

        return devices;
    }

    /// <summary>
    /// Selects an audio output device by its identifier.
    /// Pass an empty string or null to use the system default.
    /// </summary>
    public void SetAudioDevice(string? deviceId)
    {
        // Capture current volume/mute before switching — LibVLC resets
        // volume when the audio output module changes.
        var savedVolume = _mediaPlayer.Volume;
        var savedMute = _isMuted;

        if (string.IsNullOrEmpty(deviceId))
        {
            foreach (var output in _libVlc.AudioOutputs)
            {
                _mediaPlayer.SetAudioOutput(output.Name);
                break;
            }
        }
        else
        {
            foreach (var output in _libVlc.AudioOutputs)
            {
                foreach (var dev in _libVlc.AudioOutputDevices(output.Name))
                {
                    if (dev.DeviceIdentifier == deviceId)
                    {
                        _mediaPlayer.SetAudioOutput(output.Name);
                        _mediaPlayer.SetOutputDevice(deviceId);
                        goto done;
                    }
                }
            }
        }

        done:
        // Re-apply volume and mute after the output switch
        _mediaPlayer.Volume = savedVolume;
        if (savedMute)
            _mediaPlayer.SetAudioTrack(-1);
    }

    private void AttachEvents()
    {
        _mediaPlayer.Playing += (_, _) =>
        {
            SetLoadState(LoadState.Complete);
            SetState(PlaybackState.Playing);
        };
        _mediaPlayer.Paused += (_, _) => SetState(PlaybackState.Paused);
        _mediaPlayer.Stopped += (_, _) => SetState(PlaybackState.Stopped);
        _mediaPlayer.Opening += (_, _) => SetLoadState(LoadState.Opening);
        _mediaPlayer.Buffering += (_, e) =>
        {
            // LibVLC fires Buffering with 100% when buffering is complete.
            if (e.Cache < 100f)
                SetLoadState(LoadState.Buffering);
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
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetLoadState(LoadState.Error);
                ErrorOccurred?.Invoke(this, $"Playback error on: {_currentSource?.Uri}");
            });
        };

        _mediaPlayer.TimeChanged += (_, e) =>
        {
            var position = TimeSpan.FromMilliseconds(e.Time);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                PositionChanged?.Invoke(this, position);
            });
        };
    }

    private void SetState(PlaybackState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;

        Debug.WriteLine($"[VlcPlayer.SetState] {oldState} -> {newState}, thread={Environment.CurrentManagedThreadId}");
        _state = newState;

        // Fire the event asynchronously on the thread pool. LibVLC often
        // fires state-change callbacks synchronously from within Stop()/Play(),
        // and subscribers (e.g. UI view-models) may try to marshal back to
        // the UI thread or re-enter playback methods. Invoking inline would
        // deadlock when _playbackLock is held by the caller.
        var handler = StateChanged;
        if (handler is not null)
        {
            var args = new PlaybackStateChangedEventArgs(oldState, newState);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Debug.WriteLine($"[VlcPlayer.SetState] Firing StateChanged ({oldState} -> {newState}) on thread={Environment.CurrentManagedThreadId}");
                handler.Invoke(this, args);
            });
        }
    }

    private void SetLoadState(LoadState newState)
    {
        var oldState = _loadState;
        if (oldState == newState) return;

        Debug.WriteLine($"[VlcPlayer.SetLoadState] {oldState} -> {newState}, thread={Environment.CurrentManagedThreadId}");
        _loadState = newState;

        var handler = LoadStateChanged;
        if (handler is not null)
        {
            var args = new LoadStateChangedEventArgs(oldState, newState);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Debug.WriteLine($"[VlcPlayer.SetLoadState] Firing LoadStateChanged ({oldState} -> {newState}) on thread={Environment.CurrentManagedThreadId}");
                handler.Invoke(this, args);
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Debug.WriteLine($"[VlcPlayer.DisposeAsync] Called — disposed={_disposed}, state={_state}, thread={Environment.CurrentManagedThreadId}");
        if (_disposed) return;
        _disposed = true;

        Debug.WriteLine("[VlcPlayer.DisposeAsync] Stopping _mediaPlayer...");
        await Task.Run(() => _mediaPlayer.Stop()).ConfigureAwait(false);
        Debug.WriteLine("[VlcPlayer.DisposeAsync] Disposing _mediaPlayer...");
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _playbackLock.Dispose();
        Debug.WriteLine("[VlcPlayer.DisposeAsync] DisposeAsync complete");
    }

    public Task UnmuteAsync()
    {
        IsMuted = false;
        return Task.CompletedTask;
    }

    public Task MuteAsync()
    {
        IsMuted = true;
        return Task.CompletedTask;
    }
}

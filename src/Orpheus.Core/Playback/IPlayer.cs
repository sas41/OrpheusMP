using Orpheus.Core.Media;

namespace Orpheus.Core.Playback;

/// <summary>
/// Core audio player abstraction. Backend-agnostic interface for media playback.
/// Implementations wrap specific engines (LibVLC, FFmpeg, etc.).
/// </summary>
public interface IPlayer : IAsyncDisposable
{
    /// <summary>
    /// Current transport state (playing, paused, stopped).
    /// </summary>
    PlaybackState PlaybackState { get; }

    /// <summary>
    /// Current media loading state (opening, buffering, complete, error).
    /// </summary>
    LoadState LoadState { get; }

    /// <summary>
    /// Current playback position.
    /// </summary>
    TimeSpan PlaybackPosition { get; }

    /// <summary>
    /// Total duration of the current media. Null for live streams.
    /// </summary>
    TimeSpan? MediaDuration { get; }

    /// <summary>
    /// Volume level from 0 (muted) to 100 (maximum).
    /// </summary>
    int Volume { get; set; }

    /// <summary>
    /// The currently loaded media source, or null if nothing is loaded.
    /// </summary>
    MediaSource? CurrentSource { get; }

    /// <summary>
    /// Load and start playing a media source.
    /// </summary>
    Task PlayAsync(MediaSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause playback.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resume playback from a paused state.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Stop playback and unload the current media.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Seek to a specific position in the media.
    /// </summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>
    /// Gets the current audio output device identifier, or null for system default.
    /// </summary>
    string? GetCurrentAudioDevice();

    /// <summary>
    /// Set the audio output device by its identifier.
    /// Pass null or empty to use the system default.
    /// </summary>
    void SetAudioDevice(string? deviceId);

    /// <summary>
    /// Get all audio output devices.
    /// </summary>
    IReadOnlyList<(string Id, string Description)> GetAudioOutputDevices();

    /// <summary>
    /// Fired when the transport state changes (playing, paused, stopped).
    /// </summary>
    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Fired when the media loading state changes (opening, buffering, complete, error).
    /// </summary>
    event EventHandler<LoadStateChangedEventArgs>? LoadStateChanged;

    /// <summary>
    /// Fired when playback position changes (typically every ~250ms during playback).
    /// </summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>
    /// Fired when the current media reaches the end.
    /// </summary>
    event EventHandler? MediaEnded;

    /// <summary>
    /// Fired when an error occurs during playback.
    /// </summary>
    event EventHandler<string>? ErrorOccurred;
}

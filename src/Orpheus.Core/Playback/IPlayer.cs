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
    PlaybackState State { get; }

    /// <summary>
    /// Current media loading state (opening, buffering, complete, error).
    /// </summary>
    LoadState LoadState { get; }

    /// <summary>
    /// Current playback position.
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// Total duration of the current media. Null for live streams.
    /// </summary>
    TimeSpan? Duration { get; }

    /// <summary>
    /// Volume level from 0 (muted) to 100 (maximum).
    /// </summary>
    int Volume { get; set; }

    /// <summary>
    /// Whether audio output is muted (independent of volume level).
    /// </summary>
    bool IsMuted { get; set; }

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
    /// Unmute all audio.
    /// </summary>
    Task UnmuteAsync();

    /// <summary>
    /// Mute all audio.
    /// </summary>
    Task MuteAsync();

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

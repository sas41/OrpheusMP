namespace Orpheus.Core.Playback;

/// <summary>
/// Represents the current state of audio playback.
/// </summary>
public enum PlaybackState
{
    /// <summary>No media is loaded.</summary>
    Stopped,

    /// <summary>Media is actively playing.</summary>
    Playing,

    /// <summary>Playback is paused.</summary>
    Paused,

    /// <summary>Media is buffering (network streams).</summary>
    Buffering,

    /// <summary>Playback encountered an error.</summary>
    Error,

    /// <summary>Media is being opened/loaded.</summary>
    Opening
}

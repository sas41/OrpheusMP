namespace Orpheus.Core.Playback;

/// <summary>
/// Transport state of audio playback (play, pause, stop).
/// </summary>
public enum PlaybackState
{
    /// <summary>No media is loaded or playback has been stopped.</summary>
    Stopped,

    /// <summary>Media is actively playing.</summary>
    Playing,

    /// <summary>Playback is paused.</summary>
    Paused
}

namespace Orpheus.Core.Playlist;

/// <summary>
/// Defines repeat behavior during playback.
/// </summary>
public enum RepeatMode
{
    /// <summary>Repeat Off — stop after the last track.</summary>
    Off,

    /// <summary>Repeat One — repeat the current track indefinitely.</summary>
    One,

    /// <summary>Repeat Playlist — loop back to the start after the last track.</summary>
    All
}

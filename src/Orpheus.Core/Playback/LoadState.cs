namespace Orpheus.Core.Playback;

/// <summary>
/// Media loading lifecycle state, independent of transport (play/pause/stop).
/// </summary>
public enum LoadState
{
    /// <summary>Media is being opened/loaded.</summary>
    Opening,

    /// <summary>Media is buffering (network streams).</summary>
    Buffering,

    /// <summary>Media has been fully loaded and is ready for playback.</summary>
    Complete,

    /// <summary>Media loading encountered an error.</summary>
    Error
}

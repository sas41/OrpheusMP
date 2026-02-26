namespace Orpheus.Core.Playlist;

/// <summary>
/// Defines how a playlist repeats after reaching the end.
/// </summary>
public enum RepeatMode
{
    /// <summary>Stop after the last track.</summary>
    None,

    /// <summary>Repeat the entire playlist.</summary>
    All,

    /// <summary>Repeat the current track.</summary>
    One
}

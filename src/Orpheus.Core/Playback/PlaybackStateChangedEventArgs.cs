namespace Orpheus.Core.Playback;

/// <summary>
/// Event data for playback state changes.
/// </summary>
public sealed class PlaybackStateChangedEventArgs : EventArgs
{
    public PlaybackState OldState { get; }
    public PlaybackState NewState { get; }

    public PlaybackStateChangedEventArgs(PlaybackState oldState, PlaybackState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

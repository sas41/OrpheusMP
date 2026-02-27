namespace Orpheus.Core.Playback;

/// <summary>
/// Event data for media loading state changes.
/// </summary>
public sealed class LoadStateChangedEventArgs : EventArgs
{
    public LoadState OldState { get; }
    public LoadState NewState { get; }

    public LoadStateChangedEventArgs(LoadState oldState, LoadState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

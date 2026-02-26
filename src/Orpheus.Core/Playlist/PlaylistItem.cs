using Orpheus.Core.Media;
using Orpheus.Core.Metadata;

namespace Orpheus.Core.Playlist;

/// <summary>
/// A single entry in a playlist, combining a media source with optional metadata.
/// </summary>
public sealed class PlaylistItem
{
    /// <summary>
    /// Unique identifier for this playlist item.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// The media source for this item.
    /// </summary>
    public required MediaSource Source { get; init; }

    /// <summary>
    /// Cached metadata for this item. May be null if not yet loaded.
    /// </summary>
    public TrackMetadata? Metadata { get; set; }

    /// <summary>
    /// Display name for this item. Falls back to source display name if metadata is unavailable.
    /// </summary>
    public string DisplayName =>
        Metadata?.ToString() ?? Source.DisplayName ?? Source.Uri.ToString();

    public override string ToString() => DisplayName;
}

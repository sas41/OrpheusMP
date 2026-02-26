namespace Orpheus.Core.Library;

/// <summary>
/// Represents a track in the media library database.
/// Combines file system location with cached metadata for fast browsing.
/// </summary>
public sealed class LibraryTrack
{
    /// <summary>
    /// Unique database identifier.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Absolute path to the audio file.
    /// </summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// File size in bytes, used for change detection.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Last modification time of the file (UTC ticks), used for change detection.
    /// </summary>
    public long LastModifiedTicks { get; set; }

    /// <summary>
    /// When this track was added to the library (UTC ticks).
    /// </summary>
    public long DateAddedTicks { get; set; }

    // --- Cached metadata ---

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? TrackCount { get; set; }
    public uint? DiscNumber { get; set; }
    public uint? Year { get; set; }
    public string? Genre { get; set; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    public int? Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public string? Codec { get; set; }

    /// <summary>
    /// Duration as a TimeSpan.
    /// </summary>
    public TimeSpan? Duration =>
        DurationMs.HasValue ? TimeSpan.FromMilliseconds(DurationMs.Value) : null;

    /// <summary>
    /// Last modification time as DateTimeOffset.
    /// </summary>
    public DateTimeOffset LastModified =>
        new(LastModifiedTicks, TimeSpan.Zero);

    /// <summary>
    /// Date added as DateTimeOffset.
    /// </summary>
    public DateTimeOffset DateAdded =>
        new(DateAddedTicks, TimeSpan.Zero);

    public override string ToString()
    {
        if (Title is not null && Artist is not null)
            return $"{Artist} - {Title}";
        if (Title is not null)
            return Title;
        return Path.GetFileNameWithoutExtension(FilePath);
    }
}

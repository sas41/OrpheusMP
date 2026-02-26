namespace Orpheus.Core.Metadata;

/// <summary>
/// Metadata for an audio track, extracted from file tags or stream info.
/// </summary>
public sealed class TrackMetadata
{
    /// <summary>Track title.</summary>
    public string? Title { get; init; }

    /// <summary>Artist(s).</summary>
    public string? Artist { get; init; }

    /// <summary>Album name.</summary>
    public string? Album { get; init; }

    /// <summary>Album artist (may differ from track artist on compilations).</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Track number within the album.</summary>
    public uint? TrackNumber { get; init; }

    /// <summary>Total tracks on the album.</summary>
    public uint? TrackCount { get; init; }

    /// <summary>Disc number.</summary>
    public uint? DiscNumber { get; init; }

    /// <summary>Year of release.</summary>
    public uint? Year { get; init; }

    /// <summary>Genre(s).</summary>
    public string? Genre { get; init; }

    /// <summary>Track duration.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Audio bitrate in kbps.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Number of audio channels.</summary>
    public int? Channels { get; init; }

    /// <summary>Audio codec name (e.g., "FLAC", "MP3", "AAC").</summary>
    public string? Codec { get; init; }

    /// <summary>Embedded album art, if present.</summary>
    public byte[]? AlbumArt { get; init; }

    /// <summary>MIME type of the album art (e.g., "image/jpeg").</summary>
    public string? AlbumArtMimeType { get; init; }

    /// <summary>User-assigned rating (0-5).</summary>
    public int? Rating { get; init; }

    /// <summary>Comment tag.</summary>
    public string? Comment { get; init; }

    /// <summary>Lyrics text.</summary>
    public string? Lyrics { get; init; }

    public override string ToString()
    {
        if (Title is not null && Artist is not null)
            return $"{Artist} - {Title}";
        if (Title is not null)
            return Title;
        return "(unknown)";
    }
}

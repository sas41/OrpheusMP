namespace Orpheus.Core.Metadata;

/// <summary>
/// Metadata reader backed by TagLib#. Handles most common audio formats.
/// </summary>
public sealed class TagLibMetadataReader : IMetadataReader
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wma",
        ".wav", ".aiff", ".ape", ".mpc", ".wv", ".tta", ".dsf", ".dff"
    };

    public bool SupportsExtension(string extension)
    {
        return SupportedExtensions.Contains(extension);
    }

    public TrackMetadata ReadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;
        var properties = file.Properties;

        byte[]? albumArt = null;
        string? albumArtMime = null;

        if (tag.Pictures.Length > 0)
        {
            var picture = tag.Pictures[0];
            albumArt = picture.Data.Data;
            albumArtMime = picture.MimeType;
        }

        return new TrackMetadata
        {
            Title = NullIfEmpty(tag.Title),
            Artist = NullIfEmpty(tag.FirstPerformer),
            Album = NullIfEmpty(tag.Album),
            AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist),
            TrackNumber = tag.Track > 0 ? tag.Track : null,
            TrackCount = tag.TrackCount > 0 ? tag.TrackCount : null,
            DiscNumber = tag.Disc > 0 ? tag.Disc : null,
            Year = tag.Year > 0 ? tag.Year : null,
            Genre = NullIfEmpty(tag.FirstGenre),
            Duration = properties.Duration > TimeSpan.Zero ? properties.Duration : null,
            Bitrate = properties.AudioBitrate > 0 ? properties.AudioBitrate : null,
            SampleRate = properties.AudioSampleRate > 0 ? properties.AudioSampleRate : null,
            Channels = properties.AudioChannels > 0 ? properties.AudioChannels : null,
            Codec = NullIfEmpty(properties.Description),
            AlbumArt = albumArt,
            AlbumArtMimeType = albumArtMime,
            Comment = NullIfEmpty(tag.Comment),
            Lyrics = NullIfEmpty(tag.Lyrics),
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

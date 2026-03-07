using System.IO;

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

    public TrackMetadata ReadFromFile(string filePath, bool readPictures = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // TagLib writes parse warnings directly to stderr with no public hook to
        // suppress them. Redirect stderr around the call to keep the console clean.
        // These messages ("MPEG header did not match synch", "ID3v2 size byte > 128")
        // are diagnostics for malformed/corrupt files — TagLib still returns whatever
        // data it could recover, so they are not actionable errors for us.
        var savedError = Console.Error;
        Console.SetError(TextWriter.Null);
        TagLib.File file;
        try
        {
            // PictureLazy defers loading embedded image bytes until .Pictures is
            // accessed. When readPictures=false we never access .Pictures, so no
            // album-art bytes are allocated — dramatically reducing GC pressure
            // during library scans over large collections.
            var readStyle = readPictures
                ? TagLib.ReadStyle.Average
                : TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy;
            file = TagLib.File.Create(filePath, readStyle);
        }
        finally
        {
            Console.SetError(savedError);
        }

        using (file)
        {
            var tag = file.Tag;
            var properties = file.Properties;

            byte[]? albumArt = null;
            string? albumArtMime = null;

            if (readPictures && tag.Pictures.Length > 0)
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
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

namespace Orpheus.Core.Media;

/// <summary>
/// Represents a media source that can be played.
/// Wraps both local file paths and remote URIs uniformly.
/// </summary>
public sealed class MediaSource
{
    /// <summary>
    /// The URI of the media source. For local files, this is a file:// URI.
    /// For network streams, this is the stream URL (http://, rtsp://, etc.).
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// The type of media source.
    /// </summary>
    public MediaSourceType Type { get; }

    /// <summary>
    /// Optional display name for this source.
    /// </summary>
    public string? DisplayName { get; set; }

    private MediaSource(Uri uri, MediaSourceType type)
    {
        Uri = uri;
        Type = type;
    }

    /// <summary>
    /// Create a media source from a local file path.
    /// </summary>
    public static MediaSource FromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Media file not found.", fullPath);

        return new MediaSource(new Uri(fullPath), MediaSourceType.LocalFile)
        {
            DisplayName = Path.GetFileNameWithoutExtension(fullPath)
        };
    }

    /// <summary>
    /// Create a media source from a URI (network stream, internet radio, etc.).
    /// </summary>
    public static MediaSource FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("URI must be absolute.", nameof(uri));

        var type = uri.Scheme switch
        {
            "file" => MediaSourceType.LocalFile,
            "http" or "https" => MediaSourceType.NetworkStream,
            "rtsp" or "rtp" => MediaSourceType.NetworkStream,
            "mms" or "mmsh" => MediaSourceType.NetworkStream,
            _ => MediaSourceType.NetworkStream
        };

        return new MediaSource(uri, type);
    }

    /// <summary>
    /// Create a media source from a URI string.
    /// </summary>
    public static MediaSource FromUri(string uri)
    {
        return FromUri(new Uri(uri));
    }

    public override string ToString() => DisplayName ?? Uri.ToString();
}

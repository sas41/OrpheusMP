namespace Orpheus.Core.Media;

/// <summary>
/// Categorizes the origin of a media source.
/// </summary>
public enum MediaSourceType
{
    /// <summary>A file on the local filesystem.</summary>
    LocalFile,

    /// <summary>A network stream (HTTP, RTSP, etc.).</summary>
    NetworkStream,

    /// <summary>An internet radio station (Icecast/Shoutcast).</summary>
    InternetRadio
}

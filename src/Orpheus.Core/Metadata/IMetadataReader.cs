namespace Orpheus.Core.Metadata;

/// <summary>
/// Reads metadata from audio files.
/// </summary>
public interface IMetadataReader
{
    /// <summary>
    /// Read metadata from a local audio file.
    /// </summary>
    TrackMetadata ReadFromFile(string filePath);

    /// <summary>
    /// Check if this reader supports the given file extension.
    /// </summary>
    bool SupportsExtension(string extension);
}

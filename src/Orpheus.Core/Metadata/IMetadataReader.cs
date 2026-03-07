namespace Orpheus.Core.Metadata;

/// <summary>
/// Reads metadata from audio files.
/// </summary>
public interface IMetadataReader
{
    /// <summary>
    /// Read metadata from a local audio file.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="readPictures">
    /// When <c>false</c> (default for scanning) picture data is skipped,
    /// avoiding large heap allocations for embedded cover art that the
    /// library database does not store.
    /// </param>
    TrackMetadata ReadFromFile(string filePath, bool readPictures = true);

    /// <summary>
    /// Check if this reader supports the given file extension.
    /// </summary>
    bool SupportsExtension(string extension);
}

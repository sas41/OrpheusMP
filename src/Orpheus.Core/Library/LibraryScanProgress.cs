namespace Orpheus.Core.Library;

/// <summary>
/// Progress information during a library scan.
/// </summary>
public sealed class LibraryScanProgress
{
    /// <summary>
    /// Total files discovered so far.
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// Files processed (metadata read) so far.
    /// </summary>
    public int ProcessedFiles { get; init; }

    /// <summary>
    /// New tracks added during this scan.
    /// </summary>
    public int NewTracks { get; init; }

    /// <summary>
    /// Tracks updated (metadata refreshed) during this scan.
    /// </summary>
    public int UpdatedTracks { get; init; }

    /// <summary>
    /// Tracks removed (file no longer exists) during this scan.
    /// </summary>
    public int RemovedTracks { get; init; }

    /// <summary>
    /// Files that failed to read.
    /// </summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// The file currently being processed.
    /// </summary>
    public string? CurrentFile { get; init; }

    /// <summary>
    /// Whether the scan is complete.
    /// </summary>
    public bool IsComplete { get; init; }
}

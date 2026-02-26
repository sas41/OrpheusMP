using Orpheus.Core.Metadata;

namespace Orpheus.Core.Library;

/// <summary>
/// Scans folders for audio files and populates the media library.
/// Supports incremental scanning (only processes new/changed files).
/// </summary>
public sealed class FolderScanner
{
    private readonly IMediaLibrary _library;
    private readonly IMetadataReader _metadataReader;

    /// <summary>
    /// File extensions to scan for.
    /// </summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wma",
        ".wav", ".aiff", ".ape", ".mpc", ".wv", ".tta", ".dsf", ".dff"
    };

    public FolderScanner(IMediaLibrary library, IMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(metadataReader);

        _library = library;
        _metadataReader = metadataReader;
    }

    /// <summary>
    /// Fired during scanning to report progress.
    /// </summary>
    public event EventHandler<LibraryScanProgress>? Progress;

    /// <summary>
    /// Scan all watched folders in the library.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _library.GetWatchedFoldersAsync(cancellationToken);
        await ScanFoldersAsync(folders, cancellationToken);
    }

    /// <summary>
    /// Scan specific folders.
    /// </summary>
    public async Task ScanFoldersAsync(
        IEnumerable<string> folderPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        // Phase 1: Discover all audio files.
        var audioFiles = new List<string>();
        foreach (var folder in folderPaths)
        {
            if (!Directory.Exists(folder)) continue;

            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)));

            audioFiles.AddRange(files);
        }

        var totalFiles = audioFiles.Count;
        var processed = 0;
        var newTracks = 0;
        var updatedTracks = 0;
        var errors = 0;

        // Phase 2: Process each file.
        foreach (var filePath in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists) continue;

                var existing = await _library.GetTrackByPathAsync(filePath, cancellationToken);

                // Skip if file hasn't changed.
                if (existing is not null &&
                    existing.FileSize == fileInfo.Length &&
                    existing.LastModifiedTicks == fileInfo.LastWriteTimeUtc.Ticks)
                {
                    processed++;
                    continue;
                }

                var metadata = _metadataReader.ReadFromFile(filePath);

                var track = existing ?? new LibraryTrack
                {
                    FilePath = fileInfo.FullName,
                    DateAddedTicks = DateTimeOffset.UtcNow.Ticks
                };

                track.FilePath = fileInfo.FullName;
                track.FileSize = fileInfo.Length;
                track.LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks;
                track.Title = metadata.Title;
                track.Artist = metadata.Artist;
                track.Album = metadata.Album;
                track.AlbumArtist = metadata.AlbumArtist;
                track.TrackNumber = metadata.TrackNumber;
                track.TrackCount = metadata.TrackCount;
                track.DiscNumber = metadata.DiscNumber;
                track.Year = metadata.Year;
                track.Genre = metadata.Genre;
                track.DurationMs = metadata.Duration.HasValue
                    ? (long)metadata.Duration.Value.TotalMilliseconds
                    : null;
                track.Bitrate = metadata.Bitrate;
                track.SampleRate = metadata.SampleRate;
                track.Channels = metadata.Channels;
                track.Codec = metadata.Codec;

                await _library.UpsertTrackAsync(track, cancellationToken);

                if (existing is null) newTracks++;
                else updatedTracks++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                errors++;
            }

            processed++;

            // Report progress every 10 files or on the last file.
            if (processed % 10 == 0 || processed == totalFiles)
            {
                Progress?.Invoke(this, new LibraryScanProgress
                {
                    TotalFiles = totalFiles,
                    ProcessedFiles = processed,
                    NewTracks = newTracks,
                    UpdatedTracks = updatedTracks,
                    ErrorCount = errors,
                    CurrentFile = filePath,
                    IsComplete = processed == totalFiles
                });
            }
        }

        // Phase 3: Remove tracks whose files no longer exist.
        var removed = await _library.RemoveMissingTracksAsync(cancellationToken);

        Progress?.Invoke(this, new LibraryScanProgress
        {
            TotalFiles = totalFiles,
            ProcessedFiles = totalFiles,
            NewTracks = newTracks,
            UpdatedTracks = updatedTracks,
            RemovedTracks = removed,
            ErrorCount = errors,
            IsComplete = true
        });
    }
}

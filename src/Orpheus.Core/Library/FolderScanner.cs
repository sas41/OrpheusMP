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
    /// File extensions recognized as audio files.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wma",
        ".wav", ".aiff", ".ape", ".mpc", ".wv", ".tta", ".dsf", ".dff"
    };

    /// <summary>
    /// File extensions recognized as playlist files.
    /// </summary>
    public static readonly IReadOnlySet<string> PlaylistExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls"
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
    /// Maximum number of concurrent metadata reads.
    /// Bounded to avoid overwhelming disk I/O or starving other work.
    /// </summary>
    private const int MaxParallelReads = 4;

    /// <summary>
    /// Number of tracks to accumulate before flushing to the database
    /// in a single batched transaction.
    /// </summary>
    private const int BatchSize = 50;

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

        // Phase 2: Process files with parallel metadata reads and batched DB writes.
        var pendingUpserts = new List<LibraryTrack>();

        await Parallel.ForEachAsync(
            audioFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelReads,
                CancellationToken = cancellationToken
            },
            async (filePath, ct) =>
            {
                LibraryTrack? track = null;
                var isNew = false;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists) return;

                    var existing = await _library.GetTrackByPathAsync(filePath, ct);

                    // Skip if file hasn't changed.
                    if (existing is not null &&
                        existing.FileSize == fileInfo.Length &&
                        existing.LastModifiedTicks == fileInfo.LastWriteTimeUtc.Ticks)
                    {
                        Interlocked.Increment(ref processed);
                        return;
                    }

                    var metadata = _metadataReader.ReadFromFile(filePath);

                    track = existing ?? new LibraryTrack
                    {
                        FilePath = fileInfo.FullName,
                        DateAddedTicks = DateTimeOffset.UtcNow.Ticks
                    };

                    isNew = existing is null;

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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }

                Interlocked.Increment(ref processed);

                if (track is not null)
                {
                    if (isNew) Interlocked.Increment(ref newTracks);
                    else Interlocked.Increment(ref updatedTracks);

                    // Collect tracks for batch upsert. Flush when batch is full.
                    List<LibraryTrack>? batchToFlush = null;
                    lock (pendingUpserts)
                    {
                        pendingUpserts.Add(track);
                        if (pendingUpserts.Count >= BatchSize)
                        {
                            batchToFlush = [.. pendingUpserts];
                            pendingUpserts.Clear();
                        }
                    }

                    if (batchToFlush is not null)
                    {
                        await _library.BatchUpsertTracksAsync(batchToFlush, ct);
                    }
                }

                // Report progress every 10 files or on the last file.
                var currentProcessed = Volatile.Read(ref processed);
                if (currentProcessed % 10 == 0 || currentProcessed == totalFiles)
                {
                    Progress?.Invoke(this, new LibraryScanProgress
                    {
                        TotalFiles = totalFiles,
                        ProcessedFiles = currentProcessed,
                        NewTracks = Volatile.Read(ref newTracks),
                        UpdatedTracks = Volatile.Read(ref updatedTracks),
                        ErrorCount = Volatile.Read(ref errors),
                        CurrentFile = filePath,
                        IsComplete = currentProcessed == totalFiles
                    });
                }
            });

        // Flush any remaining tracks.
        if (pendingUpserts.Count > 0)
        {
            await _library.BatchUpsertTracksAsync(pendingUpserts, cancellationToken);
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

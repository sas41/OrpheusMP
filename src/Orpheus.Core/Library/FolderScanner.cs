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
    /// Maximum number of concurrent metadata reads.
    /// TagLib# is CPU-bound per file; more threads than logical cores is
    /// counter-productive. 8 gives good throughput on mobile (4–8 cores)
    /// without monopolising them.
    /// </summary>
    private const int MaxParallelReads = 8;

    /// <summary>
    /// Number of tracks to accumulate before flushing to the database
    /// in a single batched transaction.
    /// </summary>
    private const int BatchSize = 100;

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

        // All heavy work (file I/O, metadata reads, DB writes) is pushed onto
        // the thread pool so the calling thread (Avalonia UI dispatcher) is
        // never blocked.
        await Task.Run(() => ScanFoldersInternalAsync(folderPaths, cancellationToken), cancellationToken);
    }

    private async Task ScanFoldersInternalAsync(
        IEnumerable<string> folderPaths,
        CancellationToken cancellationToken)
    {
        // Phase 0: Load a lightweight snapshot of everything already in the DB
        // (path → file size + last-modified ticks) in a single query.
        // This replaces the per-file GetTrackByPathAsync calls that previously
        // caused N database round-trips inside the parallel loop.
        var existingSnapshot = await _library.GetTrackedFileSnapshotAsync(cancellationToken);

        // Phase 1: Discover all audio files. EnumerateFiles is synchronous and
        // can block for hundreds of milliseconds on large directory trees, so we
        // run it here inside Task.Run (already on the thread pool).
        var audioFiles = new List<string>();
        foreach (var folder in folderPaths)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (AudioExtensions.Contains(Path.GetExtension(f)))
                    audioFiles.Add(f);
            }
        }

        var totalFiles = audioFiles.Count;
        var processed  = 0;
        var newTracks  = 0;
        var updatedTracks = 0;
        var errors     = 0;

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

                    var fullPath = fileInfo.FullName;

                    // Change detection: compare against the pre-loaded snapshot
                    // instead of querying the DB per file.
                    if (existingSnapshot.TryGetValue(fullPath, out var snap) &&
                        snap.FileSize == fileInfo.Length &&
                        snap.LastModifiedTicks == fileInfo.LastWriteTimeUtc.Ticks)
                    {
                        Interlocked.Increment(ref processed);
                        return;
                    }

                    // Skip album art (readPictures: false) — the library database
                    // does not store cover art, so decoding it wastes CPU time and
                    // creates large short-lived byte[] allocations that the GC
                    // must collect while scanning hundreds of files.
                    var metadata = _metadataReader.ReadFromFile(filePath, readPictures: false);

                    isNew = !existingSnapshot.ContainsKey(fullPath);

                    track = new LibraryTrack
                    {
                        FilePath          = fullPath,
                        FileSize          = fileInfo.Length,
                        LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
                        // For new tracks record now as the added timestamp.
                        // For updated tracks the DB upsert preserves the original
                        // date_added_ticks value via ON CONFLICT DO UPDATE (it only
                        // updates the columns listed, not date_added_ticks).
                        DateAddedTicks    = DateTimeOffset.UtcNow.Ticks,
                        Title       = metadata.Title,
                        Artist      = metadata.Artist,
                        Album       = metadata.Album,
                        AlbumArtist = metadata.AlbumArtist,
                        TrackNumber = metadata.TrackNumber,
                        TrackCount  = metadata.TrackCount,
                        DiscNumber  = metadata.DiscNumber,
                        Year        = metadata.Year,
                        Genre       = metadata.Genre,
                        DurationMs  = metadata.Duration.HasValue
                            ? (long)metadata.Duration.Value.TotalMilliseconds
                            : null,
                        Bitrate     = metadata.Bitrate,
                        SampleRate  = metadata.SampleRate,
                        Channels    = metadata.Channels,
                        Codec       = metadata.Codec,
                    };
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
                    else       Interlocked.Increment(ref updatedTracks);

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
                        TotalFiles     = totalFiles,
                        ProcessedFiles = currentProcessed,
                        NewTracks      = Volatile.Read(ref newTracks),
                        UpdatedTracks  = Volatile.Read(ref updatedTracks),
                        ErrorCount     = Volatile.Read(ref errors),
                        CurrentFile    = filePath,
                        IsComplete     = currentProcessed == totalFiles
                    });
                }
            });

        // Flush any remaining tracks.
        if (pendingUpserts.Count > 0)
            await _library.BatchUpsertTracksAsync(pendingUpserts, cancellationToken);

        // Phase 3: Remove tracks whose files no longer exist.
        var removed = await _library.RemoveMissingTracksAsync(cancellationToken);

        Progress?.Invoke(this, new LibraryScanProgress
        {
            TotalFiles     = totalFiles,
            ProcessedFiles = totalFiles,
            NewTracks      = newTracks,
            UpdatedTracks  = updatedTracks,
            RemovedTracks  = removed,
            ErrorCount     = errors,
            IsComplete     = true
        });
    }
}

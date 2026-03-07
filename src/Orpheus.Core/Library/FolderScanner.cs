using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orpheus.Core.Library;

/// <summary>
/// Scans folders for audio files and writes stub track records to the media library.
/// Supports incremental scanning by only reprocessing new or changed files.
///
/// This class is responsible only for filesystem discovery — it writes
/// <see cref="LibraryMetadataStatus.Pending"/> stubs to the database and fires
/// <see cref="PendingTracksAdded"/> so that a <see cref="MetadataWorker"/> can
/// enrich those stubs independently on its own thread.
/// </summary>
public sealed class FolderScanner
{
    private readonly IMediaLibrary _library;

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

    /// <summary>
    /// Maximum number of subdirectory discovery tasks to run in parallel within
    /// a single watched folder. Bounded to avoid flooding the thread pool on
    /// libraries with very wide top-level trees.
    /// </summary>
    private const int MaxParallelSubfolders = 4;

    /// <summary>
    /// Number of placeholder tracks to accumulate before flushing discovery
    /// results to the database in a single batch transaction.
    /// </summary>
    private const int DiscoveryBatchSize = 200;

    public FolderScanner(IMediaLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>
    /// Fired during scanning to report discovery progress.
    /// </summary>
    public event EventHandler<LibraryScanProgress>? Progress;

    /// <summary>
    /// Fired when new <see cref="LibraryMetadataStatus.Pending"/> tracks have been
    /// written to the database. The <see cref="MetadataWorker"/> subscribes to this
    /// so it can start enriching the new stubs without polling.
    /// </summary>
    public event EventHandler? PendingTracksAdded;

    /// <summary>
    /// Scan all watched folders in the library.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _library.GetWatchedFoldersAsync(cancellationToken).ConfigureAwait(false);
        await ScanFoldersAsync(folders, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scan specific folders.
    /// </summary>
    public Task ScanFoldersAsync(IEnumerable<string> folderPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        // Push all work — including every await continuation — onto the thread pool
        // by running the entire async state machine inside Task.Run. This prevents
        // any awaited continuation from marshalling back to the Android UI looper or
        // Avalonia's UI dispatcher, which would freeze the interface.
        return Task.Run(() => ScanFoldersInternalAsync(folderPaths, cancellationToken), cancellationToken);
    }

    private async Task ScanFoldersInternalAsync(IEnumerable<string> folderPaths, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var normalizedFolders = folderPaths
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(LibraryPathNormalizer.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"[PERF] FolderScanner: starting scan of {normalizedFolders.Count} folder(s)");

        // Load a lightweight snapshot of tracked files once so discovery can do
        // change detection without per-file database round-trips.
        var snapshotSw = Stopwatch.StartNew();
        var existingSnapshot = await _library.GetTrackedFileSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[PERF] FolderScanner: GetTrackedFileSnapshot returned {existingSnapshot.Count} entries in {snapshotSw.ElapsedMilliseconds} ms");
        var knownPaths = new HashSet<string>(
            existingSnapshot.Keys.Where(path => IsWithinAnyFolder(path, normalizedFolders)),
            StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var totalFiles = 0;
        var newTracks = 0;
        var updatedTracks = 0;
        var hadNewPending = false;

        // Discover files across all watched folders. Each watched folder's
        // immediate subdirectories are enumerated in parallel; files directly
        // inside the watched folder root are handled on the calling task.
        foreach (var folder in normalizedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
            {
                Console.WriteLine($"[PERF] FolderScanner: folder does not exist, skipping: {folder}");
                // Report the folder as complete immediately with zero counts so the UI
                // can reflect that it was checked but doesn't exist on disk.
                PublishProgress(0, 0, 0, 0, 0, 0, null, new LibraryScanBatch(), isComplete: true, folderPath: folder);
                continue;
            }

            var folderSw = Stopwatch.StartNew();
            var folderFiles = 0;
            var folderNew = 0;
            var folderUpdated = 0;
            var batchCount = 0;

            Console.WriteLine($"[PERF] FolderScanner: starting discovery of {folder}");

            await DiscoverFolderAsync(
                folder,
                existingSnapshot,
                discoveredPaths,
                progress =>
                {
                    Interlocked.Add(ref totalFiles, progress.TotalFiles);
                    Interlocked.Add(ref newTracks, progress.NewTracks);
                    Interlocked.Add(ref updatedTracks, progress.UpdatedTracks);
                    Interlocked.Add(ref folderFiles, progress.TotalFiles);
                    Interlocked.Add(ref folderNew, progress.NewTracks);
                    Interlocked.Add(ref folderUpdated, progress.UpdatedTracks);
                    if (progress.HadNewPending)
                        hadNewPending = true;
                    var b = Interlocked.Increment(ref batchCount);
                    Console.WriteLine($"[PERF] FolderScanner: batch {b} flushed — {folderFiles} files found so far, {folderNew} new, elapsed {folderSw.ElapsedMilliseconds} ms");
                    PublishProgress(folderFiles, 0, folderNew, folderUpdated, 0, 0, progress.CurrentFile, progress.Batch, isComplete: false, folderPath: folder);
                },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[PERF] FolderScanner: finished discovery of {folder} — {folderFiles} files, {folderNew} new, {folderUpdated} updated in {folderSw.ElapsedMilliseconds} ms");

            // Emit a per-folder completion event so the UI can mark that folder done.
            PublishProgress(folderFiles, folderFiles, folderNew, folderUpdated, 0, 0, null, new LibraryScanBatch(), isComplete: true, folderPath: folder);
        }

        // Remove tracks whose files were previously known inside the scanned
        // folders but were not rediscovered during this pass (deleted files).
        var removedPaths = knownPaths.Except(discoveredPaths.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removedCount = await _library.RemoveTracksByPathAsync(removedPaths, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[PERF] FolderScanner: removed {removedCount} stale tracks");

        // Aggregate completion event (no FolderPath = applies to the whole scan).
        PublishProgress(
            totalFiles,
            totalFiles,
            newTracks,
            updatedTracks,
            removedCount,
            0,
            null,
            new LibraryScanBatch { RemovedPaths = removedPaths },
            isComplete: true,
            folderPath: null);

        Console.WriteLine($"[PERF] FolderScanner: scan complete — {totalFiles} total files, {newTracks} new, {updatedTracks} updated, {removedCount} removed in {sw.ElapsedMilliseconds} ms total");

        // Notify MetadataWorker that there are new Pending rows to process.
        if (hadNewPending)
            PendingTracksAdded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Discovers all audio files under <paramref name="rootFolder"/>, scanning its
    /// immediate subdirectories in parallel (up to <see cref="MaxParallelSubfolders"/>
    /// concurrent tasks) and files in the root itself serially.
    /// Returns aggregated counters for the root folder only (not subfolder results,
    /// which are reported via <paramref name="publish"/> as they complete).
    /// </summary>
    private async Task<FolderDiscoveryResult> DiscoverFolderAsync(
        string rootFolder,
        IReadOnlyDictionary<string, (long FileSize, long LastModifiedTicks)> existingSnapshot,
        ConcurrentDictionary<string, byte> discoveredPaths,
        Action<SubfolderProgress> publish,
        CancellationToken cancellationToken)
    {
        // Files directly in the root folder (non-recursive, depth=0)
        var rootResult = await DiscoverFilesInDirectoryAsync(
            rootFolder,
            recursive: false,
            existingSnapshot,
            discoveredPaths,
            publish,
            cancellationToken).ConfigureAwait(false);

        // Immediate subdirectories are each scanned recursively in their own task,
        // bounded by MaxParallelSubfolders.
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(rootFolder);
        }
        catch
        {
            return rootResult;
        }

        await Parallel.ForEachAsync(
            subdirs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelSubfolders,
                CancellationToken = cancellationToken,
            },
            async (subdir, ct) =>
            {
                await DiscoverFilesInDirectoryAsync(
                    subdir,
                    recursive: true,
                    existingSnapshot,
                    discoveredPaths,
                    publish,
                    ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return rootResult;
    }

    /// <summary>
    /// Scans all audio files within a single directory (optionally recursive),
    /// writing Pending stubs to the database in batches.
    /// </summary>
    private async Task<FolderDiscoveryResult> DiscoverFilesInDirectoryAsync(
        string directory,
        bool recursive,
        IReadOnlyDictionary<string, (long FileSize, long LastModifiedTicks)> existingSnapshot,
        ConcurrentDictionary<string, byte> discoveredPaths,
        Action<SubfolderProgress> publish,
        CancellationToken cancellationToken)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var totalFiles = 0;
        var newTracks = 0;
        var updatedTracks = 0;
        var hadNewPending = false;
        var pendingUpserts = new List<LibraryTrack>(DiscoveryBatchSize);
        string? currentFile = null;
        // Track what has already been published so each publish call sends a delta,
        // not the cumulative local total. The parent accumulates via Interlocked.Add,
        // so publishing the running total would inflate the folder-level counter.
        var lastPublishedTotal = 0;
        var lastPublishedNew = 0;
        var lastPublishedUpdated = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.*", searchOption);
        }
        catch
        {
            return new FolderDiscoveryResult(0, 0, 0, false);
        }

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!AudioExtensions.Contains(Path.GetExtension(filePath)))
                continue;

            var fullPath = Path.GetFullPath(filePath);
            discoveredPaths.TryAdd(fullPath, 0);
            totalFiles++;
            currentFile = fullPath;

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            // Change detection: skip files whose size and mtime match the snapshot.
            if (existingSnapshot.TryGetValue(fullPath, out var snapshot) &&
                snapshot.FileSize == fileInfo.Length &&
                snapshot.LastModifiedTicks == fileInfo.LastWriteTimeUtc.Ticks)
            {
                continue;
            }

            var isNew = !existingSnapshot.ContainsKey(fullPath);
            var stubTrack = CreateStubTrack(fileInfo);
            pendingUpserts.Add(stubTrack);
            hadNewPending = true;

            if (isNew)
                newTracks++;
            else
                updatedTracks++;

            if (pendingUpserts.Count >= DiscoveryBatchSize)
            {
                var batch = await FlushDiscoveryBatchAsync(pendingUpserts, cancellationToken).ConfigureAwait(false);
                publish(new SubfolderProgress(
                    totalFiles   - lastPublishedTotal,
                    newTracks    - lastPublishedNew,
                    updatedTracks - lastPublishedUpdated,
                    true, currentFile, batch));
                lastPublishedTotal   = totalFiles;
                lastPublishedNew     = newTracks;
                lastPublishedUpdated = updatedTracks;
            }
        }

        var unpublishedTotal   = totalFiles   - lastPublishedTotal;
        var unpublishedNew     = newTracks     - lastPublishedNew;
        var unpublishedUpdated = updatedTracks - lastPublishedUpdated;

        if (pendingUpserts.Count > 0)
        {
            var batch = await FlushDiscoveryBatchAsync(pendingUpserts, cancellationToken).ConfigureAwait(false);
            publish(new SubfolderProgress(unpublishedTotal, unpublishedNew, unpublishedUpdated, true, currentFile, batch));
        }
        else if (unpublishedTotal > 0)
        {
            publish(new SubfolderProgress(unpublishedTotal, unpublishedNew, unpublishedUpdated, false, currentFile, new LibraryScanBatch()));
        }

        return new FolderDiscoveryResult(totalFiles, newTracks, updatedTracks, hadNewPending);
    }

    private async Task<LibraryScanBatch> FlushDiscoveryBatchAsync(List<LibraryTrack> pendingUpserts, CancellationToken cancellationToken)
    {
        var batchTracks = pendingUpserts.ToArray();
        pendingUpserts.Clear();
        var dbSw = Stopwatch.StartNew();
        await _library.BatchUpsertTracksAsync(batchTracks, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[PERF] FolderScanner: BatchUpsert {batchTracks.Length} stubs took {dbSw.ElapsedMilliseconds} ms");
        return new LibraryScanBatch
        {
            UpsertedTracks = batchTracks,
            IsDiscoveryBatch = true,
        };
    }

    private void PublishProgress(
        int totalFiles,
        int processedFiles,
        int newTracks,
        int updatedTracks,
        int removedTracks,
        int errorCount,
        string? currentFile,
        LibraryScanBatch batch,
        bool isComplete,
        string? folderPath = null)
    {
        Progress?.Invoke(this, new LibraryScanProgress
        {
            Batch = batch,
            TotalFiles = totalFiles,
            ProcessedFiles = processedFiles,
            NewTracks = newTracks,
            UpdatedTracks = updatedTracks,
            RemovedTracks = removedTracks,
            ErrorCount = errorCount,
            CurrentFile = currentFile,
            IsComplete = isComplete,
            FolderPath = folderPath,
            IsFilesystemScan = true,
        });
    }

    private static LibraryTrack CreateStubTrack(FileInfo fileInfo)
    {
        var fullPath = fileInfo.FullName;
        return new LibraryTrack
        {
            FilePath = fullPath,
            FolderPath = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
            DateAddedTicks = DateTimeOffset.UtcNow.Ticks,
            MetadataStatus = LibraryMetadataStatus.Pending,
            Title = Path.GetFileNameWithoutExtension(fullPath),
        };
    }

    private static bool IsWithinAnyFolder(string path, IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            if (LibraryPathNormalizer.IsPathWithinFolder(path, folder))
                return true;
        }

        return false;
    }

    private sealed record SubfolderProgress(
        int TotalFiles,
        int NewTracks,
        int UpdatedTracks,
        bool HadNewPending,
        string? CurrentFile,
        LibraryScanBatch Batch);

    private sealed record FolderDiscoveryResult(
        int TotalFiles,
        int NewTracks,
        int UpdatedTracks,
        bool HadNewPending);
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using Orpheus.Core.Metadata;

namespace Orpheus.Core.Library;

/// <summary>
/// Scans folders for audio files and populates the media library.
/// Supports incremental scanning by only reprocessing new or changed files.
/// Uses a fast discovery pass plus background metadata enrichment.
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

    /// <summary>
    /// Maximum number of concurrent metadata reads.
    /// TagLib# is CPU-bound per file; more threads than logical cores is
    /// counter-productive. 8 gives good throughput on mobile (4-8 cores)
    /// without monopolizing them.
    /// </summary>
    private const int MaxParallelReads = 8;

    /// <summary>
    /// Number of placeholder tracks to accumulate before flushing discovery
    /// results to the database in a single batch.
    /// </summary>
    private const int DiscoveryBatchSize = 200;

    /// <summary>
    /// Number of metadata updates to accumulate before flushing them to the
    /// database in a single batch.
    /// </summary>
    private const int MetadataBatchSize = 64;

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
    public async Task ScanFoldersAsync(IEnumerable<string> folderPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        // All heavy work (file I/O, metadata reads, DB writes) is pushed onto
        // the thread pool so the calling thread (Avalonia UI dispatcher) is
        // never blocked.
        await Task.Run(() => ScanFoldersInternalAsync(folderPaths, cancellationToken), cancellationToken);
    }

    private async Task ScanFoldersInternalAsync(IEnumerable<string> folderPaths, CancellationToken cancellationToken)
    {
        var normalizedFolders = folderPaths
            .Where(static folder => !string.IsNullOrWhiteSpace(folder))
            .Select(LibraryPathNormalizer.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Load a lightweight snapshot of tracked files once so discovery can do
        // change detection without per-file database round-trips.
        var existingSnapshot = await _library.GetTrackedFileSnapshotAsync(cancellationToken);
        var knownPaths = new HashSet<string>(
            existingSnapshot.Keys.Where(path => IsWithinAnyFolder(path, normalizedFolders)),
            StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var metadataQueue = Channel.CreateBounded<PendingMetadataRead>(new BoundedChannelOptions(MaxParallelReads * MetadataBatchSize)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var totalFiles = 0;
        var processedFiles = 0;
        var newTracks = 0;
        var updatedTracks = 0;
        var errors = 0;
        string? currentFile = null;

        var discoveryTask = DiscoverFilesAsync(
            normalizedFolders,
            existingSnapshot,
            discoveredPaths,
            metadataQueue.Writer,
            progress =>
            {
                totalFiles = progress.TotalFiles;
                newTracks = progress.NewTracks;
                updatedTracks = progress.UpdatedTracks;
                currentFile = progress.CurrentFile;
                PublishProgress(totalFiles, processedFiles, newTracks, updatedTracks, 0, errors, currentFile, progress.Batch, isComplete: false);
            },
            cancellationToken);

        var workerTasks = Enumerable.Range(0, MaxParallelReads)
            .Select(_ => ProcessMetadataQueueAsync(
                metadataQueue.Reader,
                batch =>
                {
                    processedFiles += batch.UpsertedTracks.Count;
                    PublishProgress(totalFiles, processedFiles, newTracks, updatedTracks, 0, errors, currentFile, batch, isComplete: false);
                },
                filePath =>
                {
                    errors++;
                    currentFile = filePath;
                    PublishProgress(totalFiles, processedFiles, newTracks, updatedTracks, 0, errors, currentFile, new LibraryScanBatch(), isComplete: false);
                },
                cancellationToken))
            .ToArray();

        await discoveryTask;
        metadataQueue.Writer.TryComplete();
        await Task.WhenAll(workerTasks);

        processedFiles = totalFiles;

        // Remove tracks whose files were previously known inside the scanned
        // folders but were not rediscovered during this pass.
        var removedPaths = knownPaths.Except(discoveredPaths.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removedCount = await _library.RemoveTracksByPathAsync(removedPaths, cancellationToken);

        PublishProgress(
            totalFiles,
            processedFiles,
            newTracks,
            updatedTracks,
            removedCount,
            errors,
            currentFile,
            new LibraryScanBatch { RemovedPaths = removedPaths },
            isComplete: true);
    }

    private async Task DiscoverFilesAsync(
        IReadOnlyList<string> folderPaths,
        IReadOnlyDictionary<string, (long FileSize, long LastModifiedTicks)> existingSnapshot,
        ConcurrentDictionary<string, byte> discoveredPaths,
        ChannelWriter<PendingMetadataRead> metadataWriter,
        Action<DiscoveryProgress> publish,
        CancellationToken cancellationToken)
    {
        var totalFiles = 0;
        var newTracks = 0;
        var updatedTracks = 0;
        var pendingUpserts = new List<LibraryTrack>(DiscoveryBatchSize);

        foreach (var folder in folderPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!AudioExtensions.Contains(Path.GetExtension(filePath)))
                    continue;

                var fullPath = Path.GetFullPath(filePath);
                discoveredPaths.TryAdd(fullPath, 0);
                totalFiles++;

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

                // Change detection uses the preloaded snapshot instead of
                // querying the database again for each discovered file.
                if (existingSnapshot.TryGetValue(fullPath, out var snapshot) &&
                    snapshot.FileSize == fileInfo.Length &&
                    snapshot.LastModifiedTicks == fileInfo.LastWriteTimeUtc.Ticks)
                {
                    continue;
                }

                var isNew = !existingSnapshot.ContainsKey(fullPath);
                var stubTrack = CreateStubTrack(fileInfo, metadataStatus: LibraryMetadataStatus.Pending);
                pendingUpserts.Add(stubTrack);

                if (isNew)
                    newTracks++;
                else
                    updatedTracks++;

                await metadataWriter.WriteAsync(new PendingMetadataRead(fileInfo, isNew), cancellationToken);

                if (pendingUpserts.Count >= DiscoveryBatchSize)
                {
                    var batch = await FlushDiscoveryBatchAsync(pendingUpserts, cancellationToken);
                    publish(new DiscoveryProgress(totalFiles, newTracks, updatedTracks, fullPath, batch));
                }
            }
        }

        if (pendingUpserts.Count > 0)
        {
            var batch = await FlushDiscoveryBatchAsync(pendingUpserts, cancellationToken);
            publish(new DiscoveryProgress(totalFiles, newTracks, updatedTracks, batch.UpsertedTracks[^1].FilePath, batch));
        }
        else
        {
            publish(new DiscoveryProgress(totalFiles, newTracks, updatedTracks, null, new LibraryScanBatch()));
        }
    }

    private async Task ProcessMetadataQueueAsync(
        ChannelReader<PendingMetadataRead> reader,
        Action<LibraryScanBatch> onBatch,
        Action<string> onError,
        CancellationToken cancellationToken)
    {
        var pendingUpdates = new List<LibraryTrack>(MetadataBatchSize);

        await foreach (var pending in reader.ReadAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Skip album art (readPictures: false) because the library does
                // not store it, and decoding it would add avoidable CPU work and
                // short-lived allocations while scanning large folders.
                var metadata = _metadataReader.ReadFromFile(pending.File.FullName, readPictures: false);
                pendingUpdates.Add(CreateMetadataTrack(pending.File, metadata));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                pendingUpdates.Add(CreateFailedTrack(pending.File));
                onError(pending.File.FullName);
            }

            if (pendingUpdates.Count >= MetadataBatchSize)
            {
                var batch = await FlushMetadataBatchAsync(pendingUpdates, cancellationToken);
                onBatch(batch);
            }
        }

        if (pendingUpdates.Count > 0)
        {
            var batch = await FlushMetadataBatchAsync(pendingUpdates, cancellationToken);
            onBatch(batch);
        }
    }

    private async Task<LibraryScanBatch> FlushDiscoveryBatchAsync(List<LibraryTrack> pendingUpserts, CancellationToken cancellationToken)
    {
        var batchTracks = pendingUpserts.ToArray();
        pendingUpserts.Clear();
        await _library.BatchUpsertTracksAsync(batchTracks, cancellationToken);
        return new LibraryScanBatch
        {
            UpsertedTracks = batchTracks,
            IsDiscoveryBatch = true,
        };
    }

    private async Task<LibraryScanBatch> FlushMetadataBatchAsync(List<LibraryTrack> pendingUpdates, CancellationToken cancellationToken)
    {
        var batchTracks = pendingUpdates.ToArray();
        pendingUpdates.Clear();
        await _library.BatchUpsertTracksAsync(batchTracks, cancellationToken);
        return new LibraryScanBatch
        {
            UpsertedTracks = batchTracks,
            IsDiscoveryBatch = false,
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
        bool isComplete)
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
        });
    }

    private static LibraryTrack CreateStubTrack(FileInfo fileInfo, LibraryMetadataStatus metadataStatus)
    {
        var fullPath = fileInfo.FullName;
        return new LibraryTrack
        {
            FilePath = fullPath,
            FolderPath = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
            DateAddedTicks = DateTimeOffset.UtcNow.Ticks,
            MetadataStatus = metadataStatus,
            Title = Path.GetFileNameWithoutExtension(fullPath),
        };
    }

    private static LibraryTrack CreateMetadataTrack(FileInfo fileInfo, TrackMetadata metadata)
    {
        var fullPath = fileInfo.FullName;
        return new LibraryTrack
        {
            FilePath = fullPath,
            FolderPath = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
            DateAddedTicks = DateTimeOffset.UtcNow.Ticks,
            MetadataStatus = LibraryMetadataStatus.Ready,
            Title = metadata.Title ?? Path.GetFileNameWithoutExtension(fullPath),
            Artist = metadata.Artist,
            Album = metadata.Album,
            AlbumArtist = metadata.AlbumArtist,
            TrackNumber = metadata.TrackNumber,
            TrackCount = metadata.TrackCount,
            DiscNumber = metadata.DiscNumber,
            Year = metadata.Year,
            Genre = metadata.Genre,
            DurationMs = metadata.Duration.HasValue ? (long)metadata.Duration.Value.TotalMilliseconds : null,
            Bitrate = metadata.Bitrate,
            SampleRate = metadata.SampleRate,
            Channels = metadata.Channels,
            Codec = metadata.Codec,
        };
    }

    private static LibraryTrack CreateFailedTrack(FileInfo fileInfo)
    {
        var fullPath = fileInfo.FullName;
        return new LibraryTrack
        {
            FilePath = fullPath,
            FolderPath = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
            DateAddedTicks = DateTimeOffset.UtcNow.Ticks,
            MetadataStatus = LibraryMetadataStatus.Failed,
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

    private sealed record PendingMetadataRead(FileInfo File, bool IsNew);

    private sealed record DiscoveryProgress(
        int TotalFiles,
        int NewTracks,
        int UpdatedTracks,
        string? CurrentFile,
        LibraryScanBatch Batch);
}

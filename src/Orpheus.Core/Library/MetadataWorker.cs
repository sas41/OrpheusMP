using System.Diagnostics;
using System.Threading.Channels;
using Orpheus.Core.Metadata;

namespace Orpheus.Core.Library;

/// <summary>
/// Background worker that reads metadata tags for tracks whose
/// <see cref="LibraryMetadataStatus"/> is <see cref="LibraryMetadataStatus.Pending"/>.
///
/// This worker is independent of <see cref="FolderScanner"/>: it operates solely on
/// the database contents and can be triggered at any time — on app startup (to resume
/// after a crash), or in response to <see cref="FolderScanner.PendingTracksAdded"/>.
///
/// Concurrency model:
///   - A <see cref="SemaphoreSlim"/> ensures only one pass runs at a time.
///   - If <see cref="TriggerAsync"/> is called while a pass is already running,
///     a flag is set so a follow-up pass starts automatically when the current one
///     finishes. This guarantees that every batch of Pending rows is eventually processed.
///   - Within a single pass, a bounded <see cref="Channel{T}"/> feeds up to
///     <see cref="MaxParallelReads"/> parallel tag-reading workers so that TagLib#
///     CPU work is spread across cores without monopolising them.
///   - Every await uses ConfigureAwait(false) so continuations never marshal back
///     to the Android UI looper or Avalonia dispatcher — the entire pass runs on
///     thread-pool threads only.
/// </summary>
public sealed class MetadataWorker
{
    private readonly IMediaLibrary _library;
    private readonly IMetadataReader _metadataReader;

    /// <summary>
    /// Maximum concurrent TagLib# reads per pass.
    /// TagLib# is CPU-bound per file; 8 gives good throughput on 4–8 core devices.
    /// </summary>
    private const int MaxParallelReads = 8;

    /// <summary>
    /// Number of enriched tracks to accumulate before flushing to the database.
    /// </summary>
    private const int MetadataBatchSize = 64;

    // One active pass at a time.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    // Set when a trigger arrives while a pass is already running; causes a
    // follow-up pass to start immediately after the current one completes.
    private volatile bool _pendingRecheck;

    public MetadataWorker(IMediaLibrary library, IMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(metadataReader);

        _library = library;
        _metadataReader = metadataReader;
    }

    /// <summary>
    /// Fired during metadata enrichment to report progress.
    /// Uses the same <see cref="LibraryScanProgress"/> shape as <see cref="FolderScanner"/>
    /// so that existing UI progress handlers work without modification.
    /// </summary>
    public event EventHandler<LibraryScanProgress>? Progress;

    /// <summary>
    /// Request a metadata enrichment pass. If a pass is already in progress the
    /// request is coalesced: a single follow-up pass will run after the current
    /// one completes. All work is executed on thread-pool threads — this method
    /// returns immediately and never blocks the caller.
    /// </summary>
    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        // Run entirely on the thread pool so no UI-thread work is ever scheduled.
        return Task.Run(() => RunPassIfIdleAsync(cancellationToken), cancellationToken);
    }

    private async Task RunPassIfIdleAsync(CancellationToken cancellationToken)
    {
        // If the gate is not immediately available another pass is running.
        // Set the recheck flag so that pass will schedule a follow-up.
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _pendingRecheck = true;
            return;
        }

        try
        {
            do
            {
                _pendingRecheck = false;
                await RunPassAsync(cancellationToken).ConfigureAwait(false);
            }
            // Keep looping while triggers arrived during the pass.
            while (_pendingRecheck && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        var passSw = Stopwatch.StartNew();
        var pending = await _library.GetPendingTracksAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[PERF] MetadataWorker: GetPendingTracks returned {pending.Count} tracks in {passSw.ElapsedMilliseconds} ms");
        if (pending.Count == 0)
            return;

        // Load watched folders once so we can map each track back to its root folder
        // and fire per-folder progress events for the settings UI.
        var watchedFolders = await _library.GetWatchedFoldersAsync(cancellationToken).ConfigureAwait(false);

        // Build per-folder total counts from the pending list.
        var folderTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderProcessed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in watchedFolders)
        {
            folderTotals[folder] = 0;
            folderProcessed[folder] = 0;
        }
        foreach (var track in pending)
        {
            var root = FindRootFolder(track.FilePath, watchedFolders);
            if (root is not null)
                folderTotals[root] = folderTotals.GetValueOrDefault(root) + 1;
        }

        // Announce the start of a metadata pass for each folder that has pending work.
        foreach (var (folder, total) in folderTotals)
        {
            if (total > 0)
                PublishProgress(total, 0, 0, 0, 0, 0, null, new LibraryScanBatch(), isComplete: false, folderPath: folder);
        }

        // Feed all pending tracks into a bounded channel so tag workers can
        // consume them in parallel without loading every FileInfo into memory up front.
        var channel = Channel.CreateBounded<LibraryTrack>(new BoundedChannelOptions(MaxParallelReads * MetadataBatchSize)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var totalFiles = pending.Count;
        var processedFiles = 0;
        var errors = 0;
        string? currentFile = null;

        // Writer task: pushes pending tracks into the channel.
        var writerTask = Task.Run(async () =>
        {
            try
            {
                foreach (var track in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(track, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        // Reader tasks: MaxParallelReads workers drain the channel.
        var workerTasks = Enumerable.Range(0, MaxParallelReads)
            .Select(_ => Task.Run(() => ProcessMetadataQueueAsync(
                channel.Reader,
                batch =>
                {
                    Interlocked.Add(ref processedFiles, batch.UpsertedTracks.Count);

                    // Fire per-folder progress events for each root folder affected
                    // by this batch so the settings UI can update individual bars.
                    var byFolder = batch.UpsertedTracks
                        .GroupBy(t => FindRootFolder(t.FilePath, watchedFolders) ?? "")
                        .Where(g => g.Key.Length > 0);

                    foreach (var group in byFolder)
                    {
                        lock (folderProcessed)
                            folderProcessed[group.Key] = folderProcessed.GetValueOrDefault(group.Key) + group.Count();

                        var folderDone = folderProcessed[group.Key];
                        var folderTotal = folderTotals.GetValueOrDefault(group.Key);
                        var folderBatch = new LibraryScanBatch { UpsertedTracks = group.ToArray() };
                        PublishProgress(folderTotal, folderDone, 0, 0, 0, errors, currentFile, folderBatch, isComplete: folderDone >= folderTotal, folderPath: group.Key);
                    }
                },
                filePath =>
                {
                    Interlocked.Increment(ref errors);
                    currentFile = filePath;
                    var root = FindRootFolder(filePath, watchedFolders);
                    PublishProgress(
                        root is not null ? folderTotals.GetValueOrDefault(root) : totalFiles,
                        root is not null ? folderProcessed.GetValueOrDefault(root) : processedFiles,
                        0, 0, 0, errors, currentFile, new LibraryScanBatch(), isComplete: false, folderPath: root);
                },
                cancellationToken), cancellationToken))
            .ToArray();

        await writerTask.ConfigureAwait(false);
        await Task.WhenAll(workerTasks).ConfigureAwait(false);

        // Emit a final completion event per folder.
        foreach (var (folder, total) in folderTotals)
        {
            if (total > 0)
                PublishProgress(total, total, 0, 0, 0, 0, null, new LibraryScanBatch(), isComplete: true, folderPath: folder);
        }

        Console.WriteLine($"[PERF] MetadataWorker: pass complete — {pending.Count} tracks enriched in {passSw.ElapsedMilliseconds} ms total");
    }

    /// <summary>
    /// Returns the watched-folder root that contains the given file path,
    /// or null if no watched folder matches.
    /// </summary>
    private static string? FindRootFolder(string filePath, IReadOnlyList<string> watchedFolders)
    {
        foreach (var folder in watchedFolders)
        {
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && (filePath.Length == folder.Length || filePath[folder.Length] is '/' or '\\'))
            {
                return folder;
            }
        }
        return null;
    }

    private async Task ProcessMetadataQueueAsync(
        ChannelReader<LibraryTrack> reader,
        Action<LibraryScanBatch> onBatch,
        Action<string> onError,
        CancellationToken cancellationToken)
    {
        var pendingUpdates = new List<LibraryTrack>(MetadataBatchSize);

        await foreach (var track in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip tracks whose file no longer exists — they will be removed on the
            // next filesystem scan pass.
            if (!File.Exists(track.FilePath))
                continue;

            try
            {
                // readPictures: false — album art is not stored in the library;
                // decoding it would add avoidable CPU work and allocations.
                var metadata = _metadataReader.ReadFromFile(track.FilePath, readPictures: false);
                pendingUpdates.Add(CreateMetadataTrack(track, metadata));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                pendingUpdates.Add(CreateFailedTrack(track));
                onError(track.FilePath);
            }

            if (pendingUpdates.Count >= MetadataBatchSize)
            {
                var batch = await FlushMetadataBatchAsync(pendingUpdates, cancellationToken).ConfigureAwait(false);
                onBatch(batch);
            }
        }

        if (pendingUpdates.Count > 0)
        {
            var batch = await FlushMetadataBatchAsync(pendingUpdates, cancellationToken).ConfigureAwait(false);
            onBatch(batch);
        }
    }

    private async Task<LibraryScanBatch> FlushMetadataBatchAsync(
        List<LibraryTrack> pendingUpdates,
        CancellationToken cancellationToken)
    {
        var batchTracks = pendingUpdates.ToArray();
        pendingUpdates.Clear();
        var dbSw = Stopwatch.StartNew();
        await _library.BatchUpsertTracksAsync(batchTracks, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[PERF] MetadataWorker: BatchUpsert {batchTracks.Length} enriched tracks took {dbSw.ElapsedMilliseconds} ms");
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
            IsFilesystemScan = false,
        });
    }

    private static LibraryTrack CreateMetadataTrack(LibraryTrack stub, TrackMetadata metadata)
        => new()
        {
            Id = stub.Id,
            FilePath = stub.FilePath,
            FolderPath = stub.FolderPath,
            FileSize = stub.FileSize,
            LastModifiedTicks = stub.LastModifiedTicks,
            DateAddedTicks = stub.DateAddedTicks,
            MetadataStatus = LibraryMetadataStatus.Ready,
            Title = metadata.Title ?? Path.GetFileNameWithoutExtension(stub.FilePath),
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

    private static LibraryTrack CreateFailedTrack(LibraryTrack stub)
        => new()
        {
            Id = stub.Id,
            FilePath = stub.FilePath,
            FolderPath = stub.FolderPath,
            FileSize = stub.FileSize,
            LastModifiedTicks = stub.LastModifiedTicks,
            DateAddedTicks = stub.DateAddedTicks,
            MetadataStatus = LibraryMetadataStatus.Failed,
            Title = stub.Title ?? Path.GetFileNameWithoutExtension(stub.FilePath),
        };
}

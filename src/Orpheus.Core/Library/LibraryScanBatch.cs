using System.Collections.Generic;

namespace Orpheus.Core.Library;

public sealed class LibraryScanBatch
{
    public IReadOnlyList<LibraryTrack> UpsertedTracks { get; init; } = [];

    public IReadOnlyList<string> RemovedPaths { get; init; } = [];

    public bool IsDiscoveryBatch { get; init; }

    public bool IsMetadataBatch => !IsDiscoveryBatch && UpsertedTracks.Count > 0;

    public bool HasChanges => UpsertedTracks.Count > 0 || RemovedPaths.Count > 0;
}

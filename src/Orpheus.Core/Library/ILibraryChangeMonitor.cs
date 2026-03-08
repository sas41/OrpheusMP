using System;
using System.Collections.Generic;

namespace Orpheus.Core.Library;

public sealed class LibraryChangeDetectedEventArgs : EventArgs
{
    public LibraryChangeDetectedEventArgs(
        IReadOnlyList<string> folderPaths,
        bool requiresFullRescan = false)
    {
        FolderPaths = folderPaths ?? throw new ArgumentNullException(nameof(folderPaths));
        RequiresFullRescan = requiresFullRescan;
    }

    public IReadOnlyList<string> FolderPaths { get; }

    public bool RequiresFullRescan { get; }
}

public interface ILibraryChangeMonitor : IAsyncDisposable
{
    event EventHandler<LibraryChangeDetectedEventArgs>? Changed;

    void UpdateWatchedFolders(IEnumerable<string> folderPaths);
}

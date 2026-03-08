using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orpheus.Core.Library;

namespace Orpheus.Desktop;

internal sealed class DesktopFileSystemLibraryChangeMonitor : ILibraryChangeMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private List<string> _watchedFolders = [];

    public event EventHandler<LibraryChangeDetectedEventArgs>? Changed;

    public void UpdateWatchedFolders(IEnumerable<string> folderPaths)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        var normalized = folderPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
        {
            _watchedFolders = normalized;

            var desired = normalized
                .Where(Directory.Exists)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in _watchers.Keys.Except(desired).ToList())
            {
                _watchers[folder].Dispose();
                _watchers.Remove(folder);
            }

            foreach (var folder in desired)
            {
                if (_watchers.ContainsKey(folder))
                    continue;

                _watchers[folder] = CreateWatcher(folder);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();

            _watchers.Clear();
            _watchedFolders.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private FileSystemWatcher CreateWatcher(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName |
                           NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime |
                           NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        watcher.Created += OnWatcherChanged;
        watcher.Changed += OnWatcherChanged;
        watcher.Deleted += OnWatcherChanged;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private void OnWatcherChanged(object? sender, FileSystemEventArgs e)
        => RaiseIfRelevantPath(e.FullPath, sender as FileSystemWatcher);

    private void OnWatcherRenamed(object? sender, RenamedEventArgs e)
    {
        var watcher = sender as FileSystemWatcher;
        RaiseIfRelevantPath(e.OldFullPath, watcher);
        RaiseIfRelevantPath(e.FullPath, watcher);
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        List<string> watched;
        lock (_sync)
            watched = [.. _watchedFolders];

        if (watched.Count == 0)
            return;

        Changed?.Invoke(this, new LibraryChangeDetectedEventArgs(watched, requiresFullRescan: true));
    }

    private void RaiseIfRelevantPath(string? path, FileSystemWatcher? watcher)
    {
        if (path is null)
            return;

        if (!IsRelevantPath(path))
            return;

        var root = ResolveWatchedRoot(path, watcher?.Path);
        if (root is null)
            return;

        Changed?.Invoke(this, new LibraryChangeDetectedEventArgs([root]));
    }

    private static bool IsRelevantPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return true;

        return FolderScanner.AudioExtensions.Contains(ext) ||
               FolderScanner.PlaylistExtensions.Contains(ext);
    }

    private string? ResolveWatchedRoot(string path, string? watcherRoot)
    {
        lock (_sync)
        {
            foreach (var folder in _watchedFolders)
            {
                if (IsPathWithinRoot(path, folder))
                    return folder;
            }

            if (!string.IsNullOrWhiteSpace(watcherRoot))
            {
                foreach (var folder in _watchedFolders)
                {
                    if (string.Equals(folder, watcherRoot, StringComparison.OrdinalIgnoreCase))
                        return folder;
                }
            }
        }

        return null;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == root.Length ||
               path[root.Length] == Path.DirectorySeparatorChar ||
               path[root.Length] == Path.AltDirectorySeparatorChar;
    }
}

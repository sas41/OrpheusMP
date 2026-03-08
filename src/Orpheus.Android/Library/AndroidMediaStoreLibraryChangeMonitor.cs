using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Orpheus.Core.Library;

namespace Orpheus.Android;

internal sealed class AndroidMediaStoreLibraryChangeMonitor : Java.Lang.Object, ILibraryChangeMonitor
{
    private readonly Context _context;
    private readonly object _sync = new();
    private readonly MediaObserver _observer;
    private List<string> _watchedFolders = [];
    private bool _isRegistered;

    public AndroidMediaStoreLibraryChangeMonitor(Context context)
    {
        _context = context.ApplicationContext ?? context;
        _observer = new MediaObserver(this, new Handler(Looper.MainLooper!));
    }

    public event EventHandler<LibraryChangeDetectedEventArgs>? Changed;

    public void UpdateWatchedFolders(IEnumerable<string> folderPaths)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        var normalized = folderPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.TrimEnd('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
            _watchedFolders = normalized;

        SetRegistrationState(normalized.Count > 0);
    }

    public ValueTask DisposeAsync()
    {
        SetRegistrationState(false);
        _observer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetRegistrationState(bool shouldRegister)
    {
        if (shouldRegister == _isRegistered)
            return;

        var resolver = _context.ContentResolver;
        if (shouldRegister)
        {
            resolver.RegisterContentObserver(MediaStore.Audio.Media.ExternalContentUri, true, _observer);
            resolver.RegisterContentObserver(MediaStore.Files.GetContentUri("external"), true, _observer);
            _isRegistered = true;
            return;
        }

        resolver.UnregisterContentObserver(_observer);
        _isRegistered = false;
    }

    private void OnMediaStoreChanged(global::Android.Net.Uri? uri)
    {
        List<string> watched;
        lock (_sync)
            watched = [.. _watchedFolders];

        if (watched.Count == 0)
            return;

        var changedPath = TryResolvePathFromUri(uri);
        if (string.IsNullOrWhiteSpace(changedPath))
        {
            Changed?.Invoke(this, new LibraryChangeDetectedEventArgs(watched, requiresFullRescan: true));
            return;
        }

        foreach (var folder in watched)
        {
            if (IsPathWithinRoot(changedPath, folder))
            {
                Changed?.Invoke(this, new LibraryChangeDetectedEventArgs([folder]));
                return;
            }
        }
    }

    private string? TryResolvePathFromUri(global::Android.Net.Uri? uri)
    {
        if (uri is null)
            return null;

        try
        {
            string[] projection;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                projection =
                [
                    MediaStore.IMediaColumns.RelativePath,
                    MediaStore.IMediaColumns.DisplayName,
                ];
            }
            else
            {
                projection = [MediaStore.IMediaColumns.Data];
            }

            using var cursor = _context.ContentResolver.Query(uri, projection, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return null;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var relativePath = GetString(cursor, MediaStore.IMediaColumns.RelativePath);
                var displayName = GetString(cursor, MediaStore.IMediaColumns.DisplayName);
                if (string.IsNullOrWhiteSpace(relativePath))
                    return null;

                return string.IsNullOrWhiteSpace(displayName)
                    ? $"/storage/emulated/0/{relativePath.TrimEnd('/')}"
                    : $"/storage/emulated/0/{relativePath.TrimEnd('/')}/{displayName}";
            }

            return GetString(cursor, MediaStore.IMediaColumns.Data);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(ICursor cursor, string columnName)
    {
        var index = cursor.GetColumnIndex(columnName);
        return index >= 0 && !cursor.IsNull(index) ? cursor.GetString(index) : null;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == root.Length || path[root.Length] == '/';
    }

    private sealed class MediaObserver : ContentObserver
    {
        private readonly AndroidMediaStoreLibraryChangeMonitor _owner;

        public MediaObserver(AndroidMediaStoreLibraryChangeMonitor owner, Handler handler)
            : base(handler)
        {
            _owner = owner;
        }

        public override void OnChange(bool selfChange)
        {
            base.OnChange(selfChange);
            _owner.OnMediaStoreChanged(null);
        }

        public override void OnChange(bool selfChange, global::Android.Net.Uri? uri)
        {
            base.OnChange(selfChange, uri);
            _owner.OnMediaStoreChanged(uri);
        }
    }
}

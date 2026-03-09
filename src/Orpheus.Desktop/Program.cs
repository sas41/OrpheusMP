using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Orpheus.Desktop;

class Program
{
    // The pipe name is the IPC channel used to forward file paths to the
    // existing instance. The lock file provides reliable cross-platform
    // single-instance detection via an exclusive file lock.
    internal const string PipeName = "OrpheusMP_IPC";

    // Lock file written to the user's config dir (same directory as
    // config.json / state.json so it survives installs/upgrades cleanly).
    private static string LockFilePath =>
        Path.Combine(AppState.GetConfigDirectory(), "instance.lock");

    [STAThread]
    public static void Main(string[] args)
    {
        // Try to acquire an exclusive lock on a well-known file.
        // FileShare.None means only one process can hold this stream open —
        // if a second process tries to open it, it gets an IOException.
        // This is reliable on Linux, macOS, and Windows.
        FileStream? lockFile = TryAcquireLock();

        if (lockFile is null)
        {
            // Another instance is already running — forward the file path and exit.
            ForwardToPrimaryInstance(args);
            return;
        }

        // We are the primary instance — run the full Avalonia application.
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Release and delete the lock file so the next launch starts cleanly.
            lockFile.Dispose();
            try { File.Delete(LockFilePath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Attempts to create/open the lock file with exclusive access.
    /// Returns the open <see cref="FileStream"/> on success, or null if
    /// another instance already holds the lock.
    /// </summary>
    private static FileStream? TryAcquireLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockFilePath)!);
            return new FileStream(
                LockFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Sends the file path (or an empty "activate" token) to the primary
    /// instance via a named pipe and returns.
    /// </summary>
    private static void ForwardToPrimaryInstance(string[] args)
    {
        // Resolve the file argument the same way the primary instance would.
        string message = ResolveFileLaunchArg(args) ?? string.Empty;

        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName:      ".",
                pipeName:        PipeName,
                direction:       PipeDirection.Out,
                options:         PipeOptions.None);

            // Give the server up to two seconds to accept (it starts on
            // Framework initialisation, which may not be done yet if the OS
            // launched both processes nearly simultaneously).
            pipe.Connect(timeout: 2_000);

            var bytes = Encoding.UTF8.GetBytes(message);
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // If IPC fails for any reason we simply exit silently — it is
            // always preferable to a duplicate instance appearing.
        }
    }

    /// <summary>
    /// Extracts a local file path from command-line arguments, mirroring the
    /// logic in <see cref="App.ResolveFileLaunchArg"/> so secondary instances
    /// can resolve the path before forwarding it.
    /// </summary>
    private static string? ResolveFileLaunchArg(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
                continue;

            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            if (File.Exists(arg))
                return arg;
        }

        return null;
    }
}

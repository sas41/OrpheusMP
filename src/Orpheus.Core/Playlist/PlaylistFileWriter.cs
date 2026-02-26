namespace Orpheus.Core.Playlist;

/// <summary>
/// Writes playlists to M3U/M3U8 and PLS files.
/// </summary>
public static class PlaylistFileWriter
{
    /// <summary>
    /// Supported output formats.
    /// </summary>
    public enum Format
    {
        M3U,
        PLS
    }

    /// <summary>
    /// Write a playlist to a file. Format is inferred from the file extension,
    /// or can be explicitly specified.
    /// </summary>
    public static void WriteFile(Playlist playlist, string filePath, Format? format = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fmt = format ?? InferFormat(filePath);
        using var writer = new StreamWriter(filePath, append: false, encoding: System.Text.Encoding.UTF8);

        switch (fmt)
        {
            case Format.M3U:
                WriteM3U(playlist, writer, filePath);
                break;
            case Format.PLS:
                WritePLS(playlist, writer, filePath);
                break;
            default:
                throw new NotSupportedException($"Unsupported format: {fmt}");
        }
    }

    private static Format InferFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".m3u" or ".m3u8" => Format.M3U,
            ".pls" => Format.PLS,
            _ => throw new NotSupportedException(
                $"Cannot infer playlist format from extension '{ext}'. Specify format explicitly.")
        };
    }

    private static void WriteM3U(Playlist playlist, StreamWriter writer, string filePath)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

        writer.WriteLine("#EXTM3U");

        if (playlist.Name is not null)
            writer.WriteLine($"#PLAYLIST:{playlist.Name}");

        foreach (var item in playlist)
        {
            var durationSeconds = item.Metadata?.Duration is not null
                ? (int)item.Metadata.Duration.Value.TotalSeconds
                : -1;

            var displayName = item.Metadata?.ToString() ?? item.DisplayName;
            writer.WriteLine($"#EXTINF:{durationSeconds},{displayName}");

            var path = GetWritablePath(item.Source.Uri, baseDir);
            writer.WriteLine(path);
        }
    }

    private static void WritePLS(Playlist playlist, StreamWriter writer, string filePath)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

        writer.WriteLine("[playlist]");
        writer.WriteLine();

        for (var i = 0; i < playlist.Count; i++)
        {
            var item = playlist[i];
            var num = i + 1; // PLS is 1-indexed.

            var path = GetWritablePath(item.Source.Uri, baseDir);
            writer.WriteLine($"File{num}={path}");

            var displayName = item.Metadata?.ToString() ?? item.DisplayName;
            writer.WriteLine($"Title{num}={displayName}");

            if (item.Metadata?.Duration is not null)
            {
                var seconds = (int)item.Metadata.Duration.Value.TotalSeconds;
                writer.WriteLine($"Length{num}={seconds}");
            }
            else
            {
                writer.WriteLine($"Length{num}=-1");
            }

            writer.WriteLine();
        }

        writer.WriteLine($"NumberOfEntries={playlist.Count}");
        writer.WriteLine("Version=2");
    }

    /// <summary>
    /// For local files, try to write a relative path. For URIs, write the full URI.
    /// </summary>
    private static string GetWritablePath(Uri uri, string baseDir)
    {
        if (uri.IsFile)
        {
            var localPath = uri.LocalPath;
            try
            {
                var relativePath = Path.GetRelativePath(baseDir, localPath);
                // Only use relative if it doesn't start with ".." deeply.
                if (!relativePath.StartsWith(".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar))
                    return relativePath;
            }
            catch
            {
                // Fall through to absolute.
            }

            return localPath;
        }

        return uri.ToString();
    }
}

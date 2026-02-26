using Orpheus.Core.Media;

namespace Orpheus.Core.Playlist;

/// <summary>
/// Reads playlist files (M3U/M3U8, PLS) and returns playlist items.
/// </summary>
public static class PlaylistFileReader
{
    /// <summary>
    /// Read a playlist file and return the items.
    /// Supports M3U, M3U8, and PLS formats.
    /// </summary>
    public static IReadOnlyList<PlaylistItem> ReadFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Playlist file not found.", filePath);

        var extension = Path.GetExtension(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".m3u" or ".m3u8" => ParseM3U(filePath),
            ".pls" => ParsePLS(filePath),
            _ => throw new NotSupportedException($"Unsupported playlist format: {extension}")
        };
    }

    private static List<PlaylistItem> ParseM3U(string filePath)
    {
        var items = new List<PlaylistItem>();
        var baseDir = Path.GetDirectoryName(filePath) ?? ".";
        string? pendingTitle = null;

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
                continue;

            // Extended M3U header.
            if (line.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                continue;

            // Extended info line: #EXTINF:duration,title
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = line.IndexOf(',');
                if (commaIndex >= 0 && commaIndex < line.Length - 1)
                    pendingTitle = line[(commaIndex + 1)..].Trim();
                continue;
            }

            // Skip other comments.
            if (line.StartsWith('#'))
                continue;

            // This is a media path or URL.
            var source = CreateSource(line, baseDir, pendingTitle);
            if (source is not null)
                items.Add(new PlaylistItem { Source = source });

            pendingTitle = null;
        }

        return items;
    }

    private static List<PlaylistItem> ParsePLS(string filePath)
    {
        var items = new List<PlaylistItem>();
        var baseDir = Path.GetDirectoryName(filePath) ?? ".";
        var entries = new Dictionary<int, (string? Path, string? Title)>();

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                var (index, value) = ParsePLSEntry(line, "File");
                if (index >= 0 && value is not null)
                {
                    if (!entries.ContainsKey(index))
                        entries[index] = (value, null);
                    else
                        entries[index] = (value, entries[index].Title);
                }
            }
            else if (line.StartsWith("Title", StringComparison.OrdinalIgnoreCase))
            {
                var (index, value) = ParsePLSEntry(line, "Title");
                if (index >= 0 && value is not null)
                {
                    if (!entries.ContainsKey(index))
                        entries[index] = (null, value);
                    else
                        entries[index] = (entries[index].Path, value);
                }
            }
        }

        foreach (var kvp in entries.OrderBy(e => e.Key))
        {
            if (kvp.Value.Path is null) continue;

            var source = CreateSource(kvp.Value.Path, baseDir, kvp.Value.Title);
            if (source is not null)
                items.Add(new PlaylistItem { Source = source });
        }

        return items;
    }

    private static (int Index, string? Value) ParsePLSEntry(string line, string prefix)
    {
        // Format: File1=path or Title1=name
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0) return (-1, null);

        var indexStr = line[prefix.Length..eqIndex];
        if (!int.TryParse(indexStr, out var index)) return (-1, null);

        var value = line[(eqIndex + 1)..].Trim();
        return (index, value.Length > 0 ? value : null);
    }

    private static MediaSource? CreateSource(string pathOrUrl, string baseDir, string? displayName = null)
    {
        // Try as URI first.
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is not ("" or "file"))
        {
            var source = MediaSource.FromUri(uri);
            if (displayName is not null)
                source.DisplayName = displayName;
            return source;
        }

        // Treat as local file path (relative to playlist directory).
        var fullPath = Path.IsPathRooted(pathOrUrl)
            ? pathOrUrl
            : Path.GetFullPath(Path.Combine(baseDir, pathOrUrl));

        if (File.Exists(fullPath))
        {
            var source = MediaSource.FromFile(fullPath);
            if (displayName is not null)
                source.DisplayName = displayName;
            return source;
        }

        // File doesn't exist — skip silently.
        return null;
    }
}

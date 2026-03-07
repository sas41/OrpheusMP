using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orpheus.Core.Library;

public static class LibraryPathNormalizer
{
    public static string NormalizeFolderPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return TrimTrailingDirectorySeparators(fullPath);
    }

    public static string NormalizeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    public static IReadOnlyList<string> NormalizeDistinctFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return folders
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsPathWithinFolder(string path, string folderPath)
    {
        var normalizedPath = NormalizeFilePath(path);
        var normalizedFolder = NormalizeFolderPath(folderPath);

        if (string.Equals(normalizedPath, normalizedFolder, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!normalizedPath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        return normalizedPath.Length == normalizedFolder.Length ||
               normalizedPath[normalizedFolder.Length] == Path.DirectorySeparatorChar ||
               normalizedPath[normalizedFolder.Length] == Path.AltDirectorySeparatorChar;
    }

    private static string TrimTrailingDirectorySeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

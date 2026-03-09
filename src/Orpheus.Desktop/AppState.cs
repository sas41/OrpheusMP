using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orpheus.Desktop;

/// <summary>
/// Transient application state stored as JSON in the platform config directory.
/// Unlike <see cref="AppConfig"/> (permanent user preferences), this file holds
/// session state that changes frequently: volume, tree expansion, last playback
/// position, play queue contents, etc.
/// <para>
///   Linux:   ~/.config/OrpheusMP/state.json
///   macOS:   ~/Library/Application Support/OrpheusMP/state.json
///   Windows: %APPDATA%\OrpheusMP\state.json
/// </para>
/// </summary>
public sealed class AppState
{
    // ── Library tree ─────────────────────────────────────────

    /// <summary>Paths of library tree nodes that were expanded in the last session.</summary>
    [JsonPropertyName("expandedPaths")]
    public List<string> ExpandedPaths { get; set; } = new();

    // ── Playback ─────────────────────────────────────────────

    /// <summary>Volume level 0–100, persisted across sessions.</summary>
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 72;

    /// <summary>Audio output device identifier, or null/empty for system default.</summary>
    [JsonPropertyName("audioDevice")]
    public string? AudioDevice { get; set; }

    // ── Play queue restoration ───────────────────────────────

    /// <summary>File paths of tracks in the play queue, in order.</summary>
    [JsonPropertyName("queuePaths")]
    public List<string> QueuePaths { get; set; } = new();

    /// <summary>Index of the track that was playing (or selected) when the app closed.</summary>
    [JsonPropertyName("queueIndex")]
    public int QueueIndex { get; set; } = -1;

    /// <summary>Playback position in seconds within the last-playing track.</summary>
    [JsonPropertyName("playbackPositionSeconds")]
    public double PlaybackPositionSeconds { get; set; }

    // ── Window geometry ────────────────────────────────────────

    /// <summary>Window X position (pixels). Null on fresh install.</summary>
    [JsonPropertyName("windowX")]
    public double? WindowX { get; set; }

    /// <summary>Window Y position (pixels). Null on fresh install.</summary>
    [JsonPropertyName("windowY")]
    public double? WindowY { get; set; }

    /// <summary>Window width (pixels). Null on fresh install.</summary>
    [JsonPropertyName("windowWidth")]
    public double? WindowWidth { get; set; }

    /// <summary>Window height (pixels). Null on fresh install.</summary>
    [JsonPropertyName("windowHeight")]
    public double? WindowHeight { get; set; }

    /// <summary>Whether the window was maximized.</summary>
    [JsonPropertyName("windowMaximized")]
    public bool WindowMaximized { get; set; }

    // ── Serialization ────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the state from disk, returning defaults if the file is missing or invalid.
    /// </summary>
    public static AppState Load()
    {
        var path = GetStatePath();
        if (!File.Exists(path))
            return new AppState();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    /// <summary>
    /// Persists the current state to disk.  Creates the directory if needed.
    /// </summary>
    public void Save()
    {
        var path = GetStatePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    // ── Path helpers ─────────────────────────────────────────

    private static string GetStatePath()
    {
        return Path.Combine(GetConfigDirectory(), "state.json");
    }

    internal static string GetConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrpheusMP");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrpheusMP");
        }

        // Linux / FreeBSD
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfig))
            xdgConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(xdgConfig, "OrpheusMP");
    }
}

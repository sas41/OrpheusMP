using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orpheus.Desktop;

/// <summary>
/// Permanent user preferences stored as JSON in the platform config directory.
/// Transient session state (volume, tree expansion, playback position, etc.)
/// lives in <see cref="AppState"/> instead.
/// <para>
///   Linux:   ~/.config/OrpheusMP/config.json
///   macOS:   ~/Library/Application Support/OrpheusMP/config.json
///   Windows: %APPDATA%\OrpheusMP\config.json
/// </para>
/// </summary>
public sealed class AppConfig
{
    /// <summary>Name of the active layout (e.g. "Muse", "Berry").</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = Theming.ThemeManager.DefaultLayout;

    /// <summary>Optional color variant for the layout (e.g. "Ember", "Midnight"), or null for the default palette.</summary>
    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    /// <summary>Audio output device identifier, or null/empty for system default.</summary>
    [JsonPropertyName("audioDevice")]
    public string? AudioDevice { get; set; }

    /// <summary>Language/locale code (e.g. "en", "de", "ja"), or null for English default.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // ── Queue display ────────────────────────────────────────

    /// <summary>Queue primary text mode: "Title" or "FileName".</summary>
    [JsonPropertyName("queueDisplayMode")]
    public string QueueDisplayMode { get; set; } = "Title";

    /// <summary>Whether to show secondary text in the play queue.</summary>
    [JsonPropertyName("queueShowSecondaryText")]
    public bool QueueShowSecondaryText { get; set; } = true;

    // ── General ──────────────────────────────────────────────

    /// <summary>Whether the tray icon is enabled (minimize to tray on close).</summary>
    [JsonPropertyName("enableTrayIcon")]
    public bool EnableTrayIcon { get; set; } = true;

    // ── Library tree ────────────────────────────────────────

    /// <summary>Whether to show individual audio files in the library tree.</summary>
    [JsonPropertyName("showLibraryFiles")]
    public bool ShowLibraryFiles { get; set; } = false;

    // ── Track list columns ───────────────────────────────────

    [JsonPropertyName("showTitle")]
    public bool ShowTitle { get; set; } = true;

    [JsonPropertyName("showArtist")]
    public bool ShowArtist { get; set; } = true;

    [JsonPropertyName("showAlbum")]
    public bool ShowAlbum { get; set; } = true;

    [JsonPropertyName("showFileName")]
    public bool ShowFileName { get; set; } = false;

    [JsonPropertyName("showLength")]
    public bool ShowLength { get; set; } = true;

    [JsonPropertyName("showFormat")]
    public bool ShowFormat { get; set; } = true;

    [JsonPropertyName("showTrackNumber")]
    public bool ShowTrackNumber { get; set; } = false;

    [JsonPropertyName("showDiscNumber")]
    public bool ShowDiscNumber { get; set; } = false;

    [JsonPropertyName("showYear")]
    public bool ShowYear { get; set; } = false;

    [JsonPropertyName("showGenre")]
    public bool ShowGenre { get; set; } = false;

    [JsonPropertyName("showBitrate")]
    public bool ShowBitrate { get; set; } = false;

    // ── Serialization ────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the config from disk, returning defaults if the file is missing or invalid.
    /// </summary>
    public static AppConfig Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>
    /// Persists the current settings to disk.  Creates the directory if needed.
    /// </summary>
    public void Save()
    {
        var path = GetSettingsPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    // ── Path helpers ─────────────────────────────────────────

    private static string GetSettingsPath()
    {
        return Path.Combine(GetConfigDirectory(), "config.json");
    }

    private static string GetConfigDirectory()
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

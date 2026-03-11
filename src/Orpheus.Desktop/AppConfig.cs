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

    /// <summary>Language/locale code (e.g. "en", "de", "ja"), or null for English default.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // ── Queue display ────────────────────────────────────────

    /// <summary>Queue primary text mode: "TitleAlbum", "FileNameFolder", or "TitleAlbumWithFallback".</summary>
    [JsonPropertyName("queueDisplayMode")]
    public string QueueDisplayMode { get; set; } = "TitleAlbumWithFallback";

    /// <summary>Whether to show secondary text in the play queue.</summary>
    [JsonPropertyName("queueShowSecondaryText")]
    public bool QueueShowSecondaryText { get; set; } = true;

    // ── General ──────────────────────────────────────────────

    /// <summary>Whether the tray icon is enabled (minimize to tray on close).</summary>
    [JsonPropertyName("enableTrayIcon")]
    public bool EnableTrayIcon { get; set; } = true;

    // ── Playback ─────────────────────────────────────────────

    /// <summary>
    /// Fade-out duration in milliseconds when stopping or skipping a track.
    /// 0 disables the fade entirely.  Range: 0–1000.
    /// </summary>
    [JsonPropertyName("fadeOutDurationMs")]
    public int FadeOutDurationMs { get; set; } = 300;

    /// <summary>
    /// Fade-in duration in milliseconds when starting a new track.
    /// 0 disables the fade entirely.  Range: 0–1000.
    /// </summary>
    [JsonPropertyName("fadeInDurationMs")]
    public int FadeInDurationMs { get; set; } = 300;

    // ── Library tree ────────────────────────────────────────

    /// <summary>Whether to show individual audio files in the library tree.</summary>
    [JsonPropertyName("showLibraryFiles")]
    public bool ShowLibraryFiles { get; set; } = false;

    // ── Shortcut bindings ────────────────────────────────────
    // Stored as human-readable combo strings: "MediaPlay", "Ctrl+Mouse4",
    // "WheelUp", etc.  Empty string = disabled (no binding).
    // The LegacyShortcutConverter reads old ushort / JSON-object formats
    // and converts them to strings.

    /// <summary>Binding for Play/Pause. Default: empty (disabled).</summary>
    [JsonPropertyName("keyPlayPause")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyPlayPause { get; set; } = "";

    /// <summary>Binding for Next Track. Default: empty (disabled).</summary>
    [JsonPropertyName("keyNextTrack")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyNextTrack { get; set; } = "";

    /// <summary>Binding for Previous Track. Default: empty (disabled).</summary>
    [JsonPropertyName("keyPreviousTrack")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyPreviousTrack { get; set; } = "";

    /// <summary>Binding for Stop. Default: empty (disabled).</summary>
    [JsonPropertyName("keyStop")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyStop { get; set; } = "";

    /// <summary>Binding for Volume Up. Default: empty (disabled).</summary>
    [JsonPropertyName("keyVolumeUp")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyVolumeUp { get; set; } = "";

    /// <summary>Binding for Volume Down. Default: empty (disabled).</summary>
    [JsonPropertyName("keyVolumeDown")]
    [JsonConverter(typeof(LegacyShortcutConverter))]
    public string KeyVolumeDown { get; set; } = "";

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

    [JsonPropertyName("trackSortField")]
    public string TrackSortField { get; set; } = nameof(Desktop.TrackSortField.Title);

    [JsonPropertyName("trackSortAscending")]
    public bool TrackSortAscending { get; set; } = true;

    [JsonPropertyName("hideMissingTitle")]
    public bool HideMissingTitle { get; set; }

    [JsonPropertyName("hideMissingArtist")]
    public bool HideMissingArtist { get; set; }

    [JsonPropertyName("hideMissingAlbum")]
    public bool HideMissingAlbum { get; set; }

    [JsonPropertyName("hideMissingGenre")]
    public bool HideMissingGenre { get; set; }

    [JsonPropertyName("hideMissingTrackNumber")]
    public bool HideMissingTrackNumber { get; set; }

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

/// <summary>
/// Reads shortcut bindings from JSON, handling three possible formats:
/// <list type="bullet">
///   <item>String — the current format ("Ctrl+WheelUp", "Mouse4", "MediaPlay").</item>
///   <item>Number (ushort) — the v1 legacy format (raw SharpHook KeyCode value).</item>
///   <item>Object — the v2 legacy format ({"key":57378,"ctrl":true}).</item>
/// </list>
/// Always writes as a plain JSON string.
/// </summary>
public sealed class LegacyShortcutConverter : JsonConverter<string>
{
    // Known SharpHook KeyCode ushort → friendly token mappings for legacy migration.
    private static readonly System.Collections.Generic.Dictionary<ushort, string> LegacyKeyMap = new()
    {
        [0xE022] = "MediaPlay",
        [0xE024] = "MediaStop",
        [0xE019] = "MediaNext",
        [0xE010] = "MediaPrevious",
        [0xE02E] = "VolumeUp",
        [0xE030] = "VolumeDown",
        [0xE020] = "VolumeMute",
    };

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Current format: plain string
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? "";

        // Legacy v1: plain ushort key code
        if (reader.TokenType == JsonTokenType.Number)
        {
            var code = reader.GetUInt16();
            return LegacyKeyMap.TryGetValue(code, out var name) ? name : $"Vc0x{code:X4}";
        }

        // Legacy v2: JSON object {"key":..., "mouse":..., "ctrl":true, ...}
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            ushort? key = null;
            ushort? mouse = null;
            bool ctrl = false, shift = false, alt = false, meta = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var prop = reader.GetString();
                reader.Read();

                switch (prop)
                {
                    case "key":   key = reader.GetUInt16();     break;
                    case "mouse": mouse = reader.GetUInt16();   break;
                    case "ctrl":  ctrl = reader.GetBoolean();   break;
                    case "shift": shift = reader.GetBoolean();  break;
                    case "alt":   alt = reader.GetBoolean();    break;
                    case "meta":  meta = reader.GetBoolean();   break;
                }
            }

            var parts = new System.Collections.Generic.List<string>(5);
            if (ctrl)  parts.Add("Ctrl");
            if (alt)   parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (meta)  parts.Add("Meta");

            if (key.HasValue)
            {
                parts.Add(LegacyKeyMap.TryGetValue(key.Value, out var kn) ? kn : $"Vc0x{key.Value:X4}");
            }
            else if (mouse.HasValue)
            {
                parts.Add($"Mouse{mouse.Value}");
            }

            return parts.Count > 0 ? string.Join("+", parts) : "";
        }

        // Null or unexpected
        if (reader.TokenType == JsonTokenType.Null)
            return "";

        reader.Skip();
        return "";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value ?? "");
    }
}

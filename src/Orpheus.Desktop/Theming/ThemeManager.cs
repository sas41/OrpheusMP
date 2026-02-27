using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Orpheus.Desktop.Theming;

/// <summary>
/// Discovers, loads, and applies layouts from built-in assets and the
/// user's config directory.
///
/// <para><b>Folder structure (per layout):</b></para>
/// <code>
///   LayoutName/
///     LayoutName.axaml                 — layout template + default palette (required)
///     LayoutName.styles.axaml          — control styles (required)
///     LayoutName.icons.axaml           — custom icons (optional, overrides built-in)
///     LayoutName.VariantName.axaml     — color variant (optional, many allowed)
///     LayoutName.usercolors.axaml      — user color overrides (optional)
/// </code>
///
/// <para><b>Resolution order (later wins for duplicate keys):</b></para>
/// <list type="number">
///   <item>Built-in default icons (PlaybackIcons.axaml)</item>
///   <item>LayoutName.axaml — base palette + MainLayout DataTemplate</item>
///   <item>LayoutName.styles.axaml — styles</item>
///   <item>LayoutName.icons.axaml — theme icon overrides (if present)</item>
///   <item>LayoutName.VariantName.axaml — color variant (if selected)</item>
///   <item>LayoutName.usercolors.axaml — user overrides (always, if present)</item>
/// </list>
///
/// <para><b>Locations searched:</b></para>
/// <list type="bullet">
///   <item>Built-in: <c>avares://Orpheus.Desktop/assets/themes/{name}/</c></item>
///   <item>User-installed: <c>{ConfigDir}/layouts/{name}/</c></item>
/// </list>
/// </summary>
public sealed class ThemeManager
{
    /// <summary>
    /// Raised after a theme (layout/variant) has been applied so that
    /// subscribers can refresh any cached theme-derived values (e.g. icon colors).
    /// </summary>
    public event EventHandler? ThemeChanged;

    private readonly Application _app;

    /// <summary>
    /// Platform-aware config directory.
    ///   Linux:   ~/.config/OrpheusMP
    ///   macOS:   ~/Library/Application Support/OrpheusMP
    ///   Windows: %APPDATA%\OrpheusMP
    /// </summary>
    private readonly string _configDirectory;

    /// <summary>
    /// Directory for user-installed layouts.
    /// </summary>
    private readonly string _layoutsDirectory;

    // Currently active resources (tracked for clean removal on theme switch).
    private readonly List<IResourceProvider> _activeDictionaries = new();
    private readonly List<IStyle> _activeStyles = new();

    /// <summary>Name of the active layout, or null.</summary>
    public string? ActiveLayout { get; private set; }

    /// <summary>Name of the active color variant, or null for the default palette.</summary>
    public string? ActiveVariant { get; private set; }

    /// <summary>Built-in layout names shipped with the application.</summary>
    public static IReadOnlyList<string> BuiltInLayouts { get; } = new[] { "Muse" };

    /// <summary>The default layout used when no config exists.</summary>
    public const string DefaultLayout = "Muse";

    /// <summary>
    /// Compiled avares:// resource paths that are known to exist.
    /// Checked before attempting to load so we avoid first-chance
    /// <see cref="Avalonia.Markup.Xaml.XamlLoadException"/> for missing optional files.
    /// </summary>
    private static readonly HashSet<string> KnownCompiledResources = new(StringComparer.OrdinalIgnoreCase)
    {
        "avares://Orpheus.Desktop/assets/icons/PlaybackIcons.axaml",
        "avares://Orpheus.Desktop/assets/themes/Muse/Muse.axaml",
        "avares://Orpheus.Desktop/assets/themes/Muse/Muse.styles.axaml",
        "avares://Orpheus.Desktop/assets/themes/Muse/Muse.Ember.axaml",
        "avares://Orpheus.Desktop/assets/themes/Muse/Muse.Berry.axaml",
        "avares://Orpheus.Desktop/assets/themes/Muse/Muse.Midnight.axaml",
    };

    /// <summary>
    /// The palette resource keys that themes are expected to define.
    /// Missing keys won't crash — they simply fall through to the Avalonia defaults.
    /// </summary>
    public static IReadOnlyList<string> PaletteKeys { get; } = new[]
    {
        "AccentColor",
        "AccentSoft",
        "AccentText",
        "AppSurfaceBrush",
        "SheenBrush",
        "PanelFill",
        "PanelStroke",
        "TextPrimary",
        "TextMuted",
        "RowHover",
        "RowSelected",
        "ButtonFill",
        "ButtonHover",
        "ButtonPressed",
        "IconColor",
        "IconActiveColor",
    };

    /// <summary>
    /// The icon resource keys that themes can override.
    /// All icons use SvgImage resources rendered via the OpacityMask technique
    /// on <c>Border.icon-glyph</c> elements. The icon color is controlled by
    /// the <c>IconColor</c> and <c>IconActiveColor</c> palette keys, which
    /// map to <c>IconColorBrush</c> / <c>IconActiveColorBrush</c> resources.
    /// Built-in defaults are provided by assets/icons/PlaybackIcons.axaml.
    /// </summary>
    public static IReadOnlyList<string> IconKeys { get; } = new[]
    {
        "PlayIconSvg",
        "PauseIconSvg",
        "StopIconSvg",
        "PreviousIconSvg",
        "NextIconSvg",
        "ShuffleIconSvg",
        "RepeatIconSvg",
        "RepeatOneIconSvg",
        "RepeatNoneIconSvg",
        "VolumeIconSvg",
    };

    public ThemeManager(Application app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _configDirectory = GetConfigDirectory();
        _layoutsDirectory = Path.Combine(_configDirectory, "layouts");
    }

    // ────────────────────────────────────────────────────────
    //  Discovery
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the names of all available layouts (built-in + user-installed).
    /// </summary>
    public IReadOnlyList<string> GetAvailableLayouts()
    {
        var layouts = new List<string>(BuiltInLayouts);

        if (Directory.Exists(_layoutsDirectory))
        {
            foreach (var dir in Directory.EnumerateDirectories(_layoutsDirectory)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var primary = Path.Combine(dir, $"{name}.axaml");
                if (File.Exists(primary) && !layouts.Contains(name, StringComparer.OrdinalIgnoreCase))
                    layouts.Add(name);
            }
        }

        return layouts;
    }

    /// <summary>
    /// Returns the color variant names available for a given layout.
    /// Does not include "usercolors" or "styles" — only real variants.
    /// </summary>
    public IReadOnlyList<string> GetVariantsForLayout(string layoutName)
    {
        var variants = new List<string>();
        var prefix = $"{layoutName}.";

        foreach (var path in EnumerateLayoutFiles(layoutName))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = fileName[prefix.Length..];

            // Skip non-variant files
            if (suffix.Equals("styles", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals("icons", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals("usercolors", StringComparison.OrdinalIgnoreCase))
                continue;

            variants.Add(suffix);
        }

        return variants;
    }

    // ────────────────────────────────────────────────────────
    //  Application
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a layout with an optional color variant.
    /// Pass null for <paramref name="variant"/> to use the default palette.
    /// </summary>
    public void Apply(string layoutName, string? variant = null)
    {
        RemoveActive();

        // 0. Built-in default icons (always loaded first so themes can override)
        var defaultIcons = LoadCompiledResourceFromUri(
            "avares://Orpheus.Desktop/assets/icons/PlaybackIcons.axaml");
        if (defaultIcons is not null)
            MergeResources(defaultIcons);

        // 1. Base layout (palette + MainLayout DataTemplate)
        var baseDictionary = LoadResourceDictionary(layoutName, $"{layoutName}.axaml");
        if (baseDictionary is null)
        {
            // Fallback to the default built-in layout
            layoutName = DefaultLayout;
            variant = null;
            baseDictionary = LoadResourceDictionary(layoutName, $"{layoutName}.axaml");
        }

        if (baseDictionary is null)
            throw new InvalidOperationException($"Failed to load the built-in {DefaultLayout} layout.");

        MergeResources(baseDictionary);

        // 2. Styles
        var styles = LoadStyles(layoutName, $"{layoutName}.styles.axaml");
        if (styles is not null)
            AddStyles(styles);

        // 3. Theme icons (override built-in defaults if present)
        var themeIcons = LoadResourceDictionary(layoutName, $"{layoutName}.icons.axaml");
        if (themeIcons is not null)
            MergeResources(themeIcons);

        // 4. Color variant (if selected)
        if (!string.IsNullOrWhiteSpace(variant))
        {
            var variantDict = LoadResourceDictionary(layoutName, $"{layoutName}.{variant}.axaml");
            if (variantDict is not null)
                MergeResources(variantDict);
        }

        // 5. User color overrides (always, if present)
        var userColors = LoadUserColors(layoutName);
        if (userColors is not null)
            MergeResources(userColors);

        ActiveLayout = layoutName;
        ActiveVariant = variant;

        // 6. Sync the Fluent theme accent so built-in control templates
        //    (DataGrid, ListBox, TreeView, TextBox, etc.) match the layout palette.
        SyncFluentAccent();

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reloads the user color overrides from disk without reloading the full layout.
    /// </summary>
    public void ReloadUserColors()
    {
        if (ActiveLayout is null)
            return;

        // Re-apply the full theme (simplest correct approach — ensures ordering)
        Apply(ActiveLayout, ActiveVariant);
    }

    /// <summary>
    /// Reads the current resolved value of each palette color key from the
    /// application's merged resources. Returns a dictionary of key → Color.
    /// Only includes keys that resolve to a <see cref="Color"/> value.
    /// </summary>
    public Dictionary<string, Color> GetCurrentPaletteColors()
    {
        var result = new Dictionary<string, Color>(PaletteKeys.Count);
        foreach (var key in PaletteKeys)
        {
            // Skip brush-only keys (AppSurfaceBrush, SheenBrush are gradients)
            if (key.EndsWith("Brush", StringComparison.Ordinal))
                continue;

            if (_app.Resources.TryGetResource(key, _app.ActualThemeVariant, out var value)
                && value is Color color)
            {
                result[key] = color;
            }
        }
        return result;
    }

    /// <summary>
    /// Writes a user colors AXAML file for the active layout containing
    /// only the specified color overrides. Then reloads the theme.
    /// </summary>
    public void SaveUserColors(string layoutName, Dictionary<string, Color> colors)
    {
        var path = GetUserColorsPath(layoutName);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path);
        writer.WriteLine("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"");
        writer.WriteLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
        writer.WriteLine();

        foreach (var (key, color) in colors)
        {
            writer.WriteLine($"  <Color x:Key=\"{key}\">#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}</Color>");
        }

        // Generate matching IconCss / IconActiveCss strings when icon colors are overridden.
        // Only set `color` so that `currentColor` in SVGs resolves correctly without
        // overriding explicit fill="none" attributes on individual elements.
        if (colors.TryGetValue("IconColor", out var iconColor))
        {
            var hex = $"#{iconColor.R:X2}{iconColor.G:X2}{iconColor.B:X2}";
            writer.WriteLine($"  <x:String x:Key=\"IconCss\">svg {{ color: {hex} !important; }}</x:String>");
        }
        if (colors.TryGetValue("IconActiveColor", out var iconActiveColor))
        {
            var hex = $"#{iconActiveColor.R:X2}{iconActiveColor.G:X2}{iconActiveColor.B:X2}";
            writer.WriteLine($"  <x:String x:Key=\"IconActiveCss\">svg {{ color: {hex} !important; }}</x:String>");
        }

        writer.WriteLine();
        writer.WriteLine("</ResourceDictionary>");

        // Reload the theme so the new colors take effect
        if (ActiveLayout is not null)
            Apply(ActiveLayout, ActiveVariant);
    }

    /// <summary>
    /// Deletes the user colors file for the given layout and reloads the theme.
    /// </summary>
    public void ClearUserColors(string layoutName)
    {
        var path = GetUserColorsPath(layoutName);
        if (File.Exists(path))
            File.Delete(path);

        if (ActiveLayout is not null)
            Apply(ActiveLayout, ActiveVariant);
    }

    // ────────────────────────────────────────────────────────
    //  Paths
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the directory for user-installed layouts.
    /// Creates it if it does not exist.
    /// </summary>
    public string EnsureLayoutsDirectory()
    {
        Directory.CreateDirectory(_layoutsDirectory);
        return _layoutsDirectory;
    }

    /// <summary>
    /// Returns the expected path for a user color override file for the given layout.
    /// The file may or may not exist on disk.
    /// </summary>
    public string GetUserColorsPath(string layoutName)
    {
        // Check user-installed location first, then fall back to config-relative path
        var userLayoutDir = Path.Combine(_layoutsDirectory, layoutName);
        return Path.Combine(userLayoutDir, $"{layoutName}.usercolors.axaml");
    }

    /// <summary>Platform-aware application config directory.</summary>
    public string ConfigDirectory => _configDirectory;

    // ────────────────────────────────────────────────────────
    //  Loading helpers
    // ────────────────────────────────────────────────────────

    private ResourceDictionary? LoadResourceDictionary(string layoutName, string fileName)
    {
        // Try built-in first
        if (IsBuiltIn(layoutName))
        {
            var rd = LoadCompiledResource(layoutName, fileName);
            if (rd is not null)
                return rd;
        }

        // Try user-installed
        var filePath = Path.Combine(_layoutsDirectory, layoutName, fileName);
        if (File.Exists(filePath))
            return LoadAxamlFromFile(filePath);

        return null;
    }

    private Styles? LoadStyles(string layoutName, string fileName)
    {
        // Try built-in
        if (IsBuiltIn(layoutName))
        {
            var avaresUri = $"avares://Orpheus.Desktop/assets/themes/{layoutName}/{fileName}";
            if (KnownCompiledResources.Contains(avaresUri))
            {
                try
                {
                    var uri = new Uri(avaresUri);
                    var include = new StyleInclude(uri) { Source = uri };
                    if (include.Loaded is Styles s)
                        return s;
                }
                catch
                {
                    // Invalid AXAML
                }
            }
        }

        // Try user-installed
        var filePath = Path.Combine(_layoutsDirectory, layoutName, fileName);
        if (File.Exists(filePath))
        {
            try
            {
                var xaml = File.ReadAllText(filePath);
                var result = AvaloniaRuntimeXamlLoader.Load(xaml, Assembly.GetExecutingAssembly());
                return result as Styles;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private ResourceDictionary? LoadUserColors(string layoutName)
    {
        // User colors can live in the user-installed layout folder
        var userPath = Path.Combine(_layoutsDirectory, layoutName, $"{layoutName}.usercolors.axaml");
        if (File.Exists(userPath))
            return LoadAxamlFromFile(userPath);

        // Or alongside the built-in layout in the config directory
        // (for users who want to override built-in layout colors without
        //  creating a full custom layout folder)
        var configPath = Path.Combine(_configDirectory, "layouts", layoutName, $"{layoutName}.usercolors.axaml");
        if (!string.Equals(configPath, userPath, StringComparison.OrdinalIgnoreCase) && File.Exists(configPath))
            return LoadAxamlFromFile(configPath);

        return null;
    }

    /// <summary>
    /// Enumerates all .axaml files for a given layout across built-in and
    /// user-installed locations.
    /// </summary>
    private IEnumerable<string> EnumerateLayoutFiles(string layoutName)
    {
        // Built-in: we know the convention, just check for known variant patterns.
        // For built-in themes we can't enumerate the avares:// filesystem easily,
        // so we rely on the user-installed directory for discovery and hardcode
        // built-in variant names as needed.
        // In the future this could read an embedded manifest.

        // User-installed
        var userDir = Path.Combine(_layoutsDirectory, layoutName);
        if (Directory.Exists(userDir))
        {
            foreach (var file in Directory.EnumerateFiles(userDir, $"{layoutName}.*.axaml"))
                yield return file;
        }

        // Built-in variants: scan compiled resources isn't practical, so we check
        // known built-in variants explicitly. Theme authors ship variants in the
        // user-installed directory.
        if (IsBuiltIn(layoutName))
        {
            // Try known built-in variant files
            var knownVariants = layoutName switch
            {
                "Muse" => new[] { "Ember", "Berry", "Midnight" },
                _ => Array.Empty<string>()
            };

            foreach (var v in knownVariants)
            {
                yield return $"{layoutName}.{v}.axaml";
            }
        }
    }

    // ────────────────────────────────────────────────────────
    //  Resource management
    // ────────────────────────────────────────────────────────

    private void MergeResources(ResourceDictionary dictionary)
    {
        GetAppMergedDictionaries().Add(dictionary);
        _activeDictionaries.Add(dictionary);
    }

    private void AddStyles(IStyle styles)
    {
        _app.Styles.Add(styles);
        _activeStyles.Add(styles);
    }

    /// <summary>
    /// Reads the theme's AccentColor and pushes it into the FluentTheme palette
    /// so all built-in Fluent control templates (DataGrid, ListBox, TreeView,
    /// TextBox, etc.) use the theme accent instead of the system default.
    /// </summary>
    private void SyncFluentAccent()
    {
        if (_app.Resources.TryGetResource("AccentColor", _app.ActualThemeVariant, out var value)
            && value is Color accentColor)
        {
            var fluent = _app.Styles.OfType<FluentTheme>().FirstOrDefault();
            if (fluent is null)
                return;

            // Each variant needs its own ColorPaletteResources instance
            // (a ResourceDictionary can only have one parent).
            // Clear existing palettes first to avoid parent conflicts.
            fluent.Palettes.Clear();
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accentColor };
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accentColor };
        }
    }

    private void RemoveActive()
    {
        var merged = GetAppMergedDictionaries();

        foreach (var dict in _activeDictionaries)
            merged.Remove(dict);
        _activeDictionaries.Clear();

        foreach (var style in _activeStyles)
            _app.Styles.Remove(style);
        _activeStyles.Clear();

        ActiveLayout = null;
        ActiveVariant = null;
    }

    private IList<IResourceProvider> GetAppMergedDictionaries()
    {
        if (_app.Resources is not ResourceDictionary rd)
        {
            rd = new ResourceDictionary();
            _app.Resources = rd;
        }

        return rd.MergedDictionaries;
    }

    // ────────────────────────────────────────────────────────
    //  Low-level loaders
    // ────────────────────────────────────────────────────────

    private static ResourceDictionary? LoadCompiledResource(string layoutName, string fileName)
    {
        var avaresUri = $"avares://Orpheus.Desktop/assets/themes/{layoutName}/{fileName}";
        if (!KnownCompiledResources.Contains(avaresUri))
            return null;

        return LoadCompiledResourceFromUri(avaresUri);
    }

    private static ResourceDictionary? LoadCompiledResourceFromUri(string avaresUri)
    {
        if (!KnownCompiledResources.Contains(avaresUri))
            return null;

        try
        {
            var uri = new Uri(avaresUri);
            var include = new ResourceInclude(uri) { Source = uri };
            return include.Loaded as ResourceDictionary;
        }
        catch
        {
            return null;
        }
    }

    private static ResourceDictionary? LoadAxamlFromFile(string filePath)
    {
        try
        {
            var xaml = File.ReadAllText(filePath);
            var result = AvaloniaRuntimeXamlLoader.Load(xaml, Assembly.GetExecutingAssembly());
            return result as ResourceDictionary;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBuiltIn(string layoutName)
    {
        return BuiltInLayouts.Any(n => string.Equals(n, layoutName, StringComparison.OrdinalIgnoreCase));
    }

    // ────────────────────────────────────────────────────────
    //  Platform config directory
    // ────────────────────────────────────────────────────────

    private static string GetConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %APPDATA%\OrpheusMP
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrpheusMP");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // ~/Library/Application Support/OrpheusMP
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrpheusMP");
        }

        // Linux / FreeBSD: ~/.config/OrpheusMP
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfig))
            xdgConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(xdgConfig, "OrpheusMP");
    }
}

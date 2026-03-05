using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Orpheus.Android;

public partial class App : Application
{
    private const string AssemblyName = "Orpheus.Android";

    public MobileViewModel? ViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        var config = MobileConfig.Load();

        if (!string.IsNullOrEmpty(config.Language))
            System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo(config.Language);

        ApplyTheme(config.Theme, config.Variant);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // ViewModel is kept alive across Activity recreations (minimize/resume).
            // MainView must be a new instance each time because the old one still
            // holds a reference to the destroyed surface's visual tree and Avalonia
            // will throw if the same control is re-parented.
            ViewModel ??= new MobileViewModel();
            singleView.MainView = new MainView { DataContext = ViewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The currently active layout and variant, used by settings.
    public string ActiveLayout { get; private set; } = "Muse";
    public string? ActiveVariant { get; private set; }

    /// <summary>
    /// Returns the known color variant names for the Muse mobile theme.
    /// These correspond to {layout}.{Variant}.axaml files in assets/themes/Muse/.
    /// </summary>
    public static IReadOnlyList<string> AvailableVariants { get; } =
        ["Default", "Ember", "Berry", "Midnight", "Plum"];

    /// <summary>Apply a new color variant on top of the already-loaded base theme.</summary>
    public void ApplyVariant(string? variant)
    {
        // Re-apply from scratch: reload base + new variant over a clean merged dict.
        // Clear only the merged entries we own (indices after FluentTheme's own resources).
        if (Resources is ResourceDictionary rd)
            rd.MergedDictionaries.Clear();

        ApplyTheme(ActiveLayout, variant == "Default" ? null : variant);

        // Persist
        ActiveVariant = variant == "Default" ? null : variant;
        var config = MobileConfig.Load();
        config.Variant = ActiveVariant;
        config.Save();
    }

    private void ApplyTheme(string layout, string? variant)
    {
        // Mobile loads the palette-only file (no desktop view-type references).
        // Falls back to Muse if the requested layout is unknown.
        var baseMerged = TryMergeResource($"avares://{AssemblyName}/assets/themes/{layout}/{layout}.mobile.axaml");
        if (!baseMerged)
        {
            layout = "Muse";
            TryMergeResource($"avares://{AssemblyName}/assets/themes/{layout}/{layout}.mobile.axaml");
        }

        ActiveLayout = layout;

        // Color variant (contains only Color / Brush entries — safe for mobile)
        if (!string.IsNullOrWhiteSpace(variant))
            TryMergeResource($"avares://{AssemblyName}/assets/themes/{layout}/{layout}.{variant}.axaml");

        SyncFluentAccent();
    }

    private bool TryMergeResource(string avaresUri)
    {
        try
        {
            var uri = new Uri(avaresUri);
            var include = new ResourceInclude(uri) { Source = uri };
            if (include.Loaded is not ResourceDictionary rd) return false;

            if (Resources is not ResourceDictionary appRd)
            {
                appRd = new ResourceDictionary();
                Resources = appRd;
            }
            appRd.MergedDictionaries.Add(rd);
            return true;
        }
        catch { return false; }
    }

    private void TryAddStyles(string avaresUri)
    {
        try
        {
            var uri = new Uri(avaresUri);
            var include = new StyleInclude(uri) { Source = uri };
            if (include.Loaded is IStyle style)
                Styles.Add(style);
        }
        catch { }
    }

    private void SyncFluentAccent()
    {
        if (!Resources.TryGetResource("AccentColor", ActualThemeVariant, out var value)
            || value is not Color accent) return;

        var fluent = Styles.OfType<IStyle>().OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null) return;

        fluent.Palettes.Clear();
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
        fluent.Palettes[ThemeVariant.Dark]  = new ColorPaletteResources { Accent = accent };
    }
}

/// <summary>
/// Minimal subset of desktop AppConfig used on mobile: theme, variant, language only.
/// Stored at the platform-appropriate app data path.
/// </summary>
internal sealed class MobileConfig
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Muse";

    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Queue/transport display mode: "TitleAlbum", "FileNameFolder", or "TitleAlbumWithFallback".</summary>
    [JsonPropertyName("trackDisplayMode")]
    public string TrackDisplayMode { get; set; } = "TitleAlbum";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MobileConfig Load()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return new MobileConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MobileConfig>(json, JsonOptions) ?? new MobileConfig();
        }
        catch { return new MobileConfig(); }
    }

    public void Save()
    {
        try
        {
            var path = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    private static string GetConfigPath()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(data))
            data = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return Path.Combine(data, "OrpheusMP", "config.json");
    }
}

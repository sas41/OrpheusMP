using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Orpheus.Core.Library;
using Orpheus.Core.Playback;
using Orpheus.Desktop.Lang;
using Orpheus.Desktop.Theming;

namespace Orpheus.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public async void OnAddFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null)
            await ViewModel.AddFolderAsync(this);
    }

    public async void OnRemoveFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var list = this.FindControl<ListBox>("FolderList");
        if (list?.SelectedItem is string folder)
            await ViewModel.RemoveFolderAsync(folder);
    }

    public async void OnResetLibrary(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ResetLibraryAsync();
    }

    public void OnApplyColors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ApplyCustomColors();
    }

    public void OnResetColors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ResetCustomColors();
    }

    public void OnLicenseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not LicenseEntry entry)
            return;

        // Build the dialog content — optional URL header + license body
        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 8 };

        if (!string.IsNullOrEmpty(entry.Url))
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Url,
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.CornflowerBlue,
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = entry.Text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
        });

        var dialog = new Window
        {
            Title = entry.DisplayName,
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = stack }
        };

        dialog.ShowDialog(this);
        list.SelectedItem = null;
    }
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ThemeManager _themeManager;
    private readonly AppConfig _config;
    private readonly VlcPlayer _player;
    private readonly IMediaLibrary _library;
    private readonly Func<Task> _onLibraryReset;
    private readonly Func<string, Task> _addLibraryFolder;

    private bool _enableTrayIcon;
    private string _selectedTheme;
    private string? _selectedVariant;
    private string? _selectedAudioDevice;
    private string _statusMessage = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        ThemeManager themeManager,
        AppConfig config,
        VlcPlayer player,
        IMediaLibrary library,
        Func<Task> onLibraryReset,
        Func<string, Task> addLibraryFolder)
    {
        _themeManager = themeManager;
        _config = config;
        _player = player;
        _library = library;
        _onLibraryReset = onLibraryReset;
        _addLibraryFolder = addLibraryFolder;

        _selectedTheme = _themeManager.ActiveLayout ?? ThemeManager.DefaultLayout;
        _selectedVariant = _themeManager.ActiveVariant ?? Resources.Default;

        Themes = new ObservableCollection<string>(_themeManager.GetAvailableLayouts());
        Variants = new ObservableCollection<string>(GetVariantsWithDefault());
        AudioDevices = new ObservableCollection<AudioDeviceItem>(GetAudioDevices());
        MusicFolders = new ObservableCollection<string>();
        Licenses = new ObservableCollection<LicenseEntry>(LoadLicenses());

        _selectedAudioDevice = _config.AudioDevice ?? "";
        _enableTrayIcon = _config.EnableTrayIcon;

        // Language selector
        LanguageOptions = new ObservableCollection<LanguageOption>(GetLanguageOptions());
        _selectedLanguage = LanguageOptions.FirstOrDefault(l => l.Code == (Resources.Culture?.Name ?? "en"))
                            ?? LanguageOptions.First();

        // Check if user colors file exists to determine initial state
        var layoutName = _themeManager.ActiveLayout ?? ThemeManager.DefaultLayout;
        var userColorsPath = _themeManager.GetUserColorsPath(layoutName);
        _customColorsEnabled = File.Exists(userColorsPath);
        if (_customColorsEnabled)
            LoadPaletteEntries();

        // Refresh locale-dependent parts when the language is changed in this window
        App.LanguageChanged += OnLanguageChanged;

        _ = LoadMusicFoldersAsync();
    }

    private void OnLanguageChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Raise PropertyChanged for every Loc* property so bindings refresh
            foreach (var name in _locPropertyNames)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            // Rebuild the Variants list so the "(Default)" entry is re-localized
            var currentIsDefault = _selectedVariant == null
                || _themeManager.GetVariantsForLayout(_selectedTheme).All(v => v != _selectedVariant);
            Variants.Clear();
            foreach (var v in GetVariantsWithDefault())
                Variants.Add(v);
            if (currentIsDefault)
            {
                _selectedVariant = Resources.Default;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVariant)));
            }

            // Rebuild the audio devices list so "System Default" is re-localized
            var savedDevice = _selectedAudioDevice;
            AudioDevices.Clear();
            foreach (var d in GetAudioDevices())
                AudioDevices.Add(d);
            _selectedAudioDevice = savedDevice;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioDevice)));
        });
    }

    // ── Localized string properties (bound from AXAML) ───────

    // All locale keys used by the Settings UI, exposed as read-only properties.
    // OnLanguageChanged raises PropertyChanged for each so the UI refreshes live.

    public string LocSettings => Resources.Settings;
    public string LocGeneral => Resources.General;
    public string LocEnableTrayIcon => Resources.EnableTrayIcon;
    public string LocLanguage => Resources.Language;
    public string LocTheme => Resources.Theme;
    public string LocAppearance => Resources.Appearance;
    public string LocColorVariant => Resources.ColorVariant;
    public string LocCustomColors => Resources.CustomColors;
    public string LocEnableCustomColorOverrides => Resources.EnableCustomColorOverrides;
    public string LocApplyColors => Resources.ApplyColors;
    public string LocResetToDefault => Resources.ResetToDefault;
    public string LocLayoutsDirectory => Resources.LayoutsDirectory;
    public string LocLibrary => Resources.Library;
    public string LocMusicFolders => Resources.MusicFolders;
    public string LocAddFolder => Resources.AddFolder;
    public string LocRemoveSelected => Resources.RemoveSelected;
    public string LocDatabase => Resources.Database;
    public string LocResetLibraryDescription => Resources.ResetLibraryDescription;
    public string LocResetLibrary => Resources.ResetLibrary;
    public string LocOutput => Resources.Output;
    public string LocAudioOutput => Resources.AudioOutput;
    public string LocOutputDevice => Resources.OutputDevice;

    private static readonly string[] _locPropertyNames =
    [
        nameof(LocSettings), nameof(LocGeneral), nameof(LocEnableTrayIcon),
        nameof(LocLanguage), nameof(LocTheme), nameof(LocAppearance),
        nameof(LocColorVariant), nameof(LocCustomColors),
        nameof(LocEnableCustomColorOverrides), nameof(LocApplyColors),
        nameof(LocResetToDefault), nameof(LocLayoutsDirectory),
        nameof(LocLibrary), nameof(LocMusicFolders), nameof(LocAddFolder),
        nameof(LocRemoveSelected), nameof(LocDatabase),
        nameof(LocResetLibraryDescription), nameof(LocResetLibrary),
        nameof(LocOutput), nameof(LocAudioOutput), nameof(LocOutputDevice),
    ];

    // ── General ──────────────────────────────────────────────

    public bool EnableTrayIcon
    {
        get => _enableTrayIcon;
        set
        {
            if (!SetField(ref _enableTrayIcon, value)) return;
            _config.EnableTrayIcon = value;
            _config.Save();

            // Notify the App to show/hide the tray icon
            if (Avalonia.Application.Current is App app)
                app.SetTrayIconEnabled(value);
        }
    }

    // ── Language ─────────────────────────────────────────────

    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    private LanguageOption _selectedLanguage;

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetField(ref _selectedLanguage, value) || value is null) return;
            App.SetLanguage(value.Code);
            _config.Language = value.Code;
            _config.Save();
        }
    }

    // ── Theme ────────────────────────────────────────────────

    public ObservableCollection<string> Themes { get; }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetField(ref _selectedTheme, value)) return;

            Variants.Clear();
            foreach (var v in GetVariantsWithDefault())
                Variants.Add(v);

            // Select localized "(Default)" so the ComboBox isn't empty
            _selectedVariant = Resources.Default;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVariant)));

            ApplyTheme();

            // Reload palette entries if custom colors are enabled
            if (_customColorsEnabled)
                LoadPaletteEntries();
        }
    }

    public ObservableCollection<string> Variants { get; }

    public string? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (!SetField(ref _selectedVariant, value)) return;
            ApplyTheme();

            // Reload palette entries if custom colors are enabled
            if (_customColorsEnabled)
                LoadPaletteEntries();
        }
    }

    private void ApplyTheme()
    {
        var variant = _selectedVariant == Resources.Default ? null : _selectedVariant;
        _themeManager.Apply(_selectedTheme, variant);
        _config.Theme = _selectedTheme;
        _config.Variant = variant;
        _config.Save();
    }

    private IEnumerable<string> GetVariantsWithDefault()
    {
        yield return Resources.Default;
        foreach (var v in _themeManager.GetVariantsForLayout(_selectedTheme))
            yield return v;
    }

    private static IEnumerable<LanguageOption> GetLanguageOptions()
    {
        return new[]
        {
            new LanguageOption("en", "English"),
            new LanguageOption("es", "Español"),
            new LanguageOption("fr", "Français"),
            new LanguageOption("de", "Deutsch"),
            new LanguageOption("it", "Italiano"),
            new LanguageOption("pt-BR", "Português (Brasil)"),
            new LanguageOption("ru", "Русский"),
            new LanguageOption("ja", "日本語"),
            new LanguageOption("zh-CN", "中文 (简体)"),
            new LanguageOption("ko", "한국어"),
            new LanguageOption("ar", "العربية"),
        };
    }

    // ── Custom Colors ────────────────────────────────────────

    private bool _customColorsEnabled;

    public bool CustomColorsEnabled
    {
        get => _customColorsEnabled;
        set
        {
            if (!SetField(ref _customColorsEnabled, value)) return;
            if (value)
                LoadPaletteEntries();
            else
                ResetCustomColors();
        }
    }

    public ObservableCollection<PaletteColorEntry> PaletteEntries { get; } = new();

    private void LoadPaletteEntries()
    {
        var colors = _themeManager.GetCurrentPaletteColors();
        PaletteEntries.Clear();
        foreach (var key in ThemeManager.PaletteKeys)
        {
            // Skip brush-only keys
            if (key.EndsWith("Brush", StringComparison.Ordinal))
                continue;

            var color = colors.TryGetValue(key, out var c) ? c : Avalonia.Media.Colors.Gray;
            PaletteEntries.Add(new PaletteColorEntry(key, color));
        }
    }

    public void ApplyCustomColors()
    {
        var layoutName = _themeManager.ActiveLayout ?? ThemeManager.DefaultLayout;
        var overrides = new Dictionary<string, Avalonia.Media.Color>();
        foreach (var entry in PaletteEntries)
        {
            if (entry.TryParseColor(out var color))
                overrides[entry.Key] = color;
        }
        _themeManager.SaveUserColors(layoutName, overrides);
        // Reload entries to reflect the new resolved colors
        LoadPaletteEntries();
    }

    public void ResetCustomColors()
    {
        var layoutName = _themeManager.ActiveLayout ?? ThemeManager.DefaultLayout;
        _themeManager.ClearUserColors(layoutName);
        PaletteEntries.Clear();
        if (_customColorsEnabled)
            LoadPaletteEntries();
    }

    public string UserLayoutsDirectory => _themeManager.EnsureLayoutsDirectory();

    // ── Audio Output ─────────────────────────────────────────

    public ObservableCollection<AudioDeviceItem> AudioDevices { get; }

    public string? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (!SetField(ref _selectedAudioDevice, value)) return;
            _player.SetAudioDevice(value);
            _config.AudioDevice = string.IsNullOrEmpty(value) ? null : value;
            _config.Save();
        }
    }

    private IEnumerable<AudioDeviceItem> GetAudioDevices()
    {
        try
        {
            return _player.GetAudioOutputDevices()
                .Select(d => new AudioDeviceItem(d.Id, d.Description));
        }
        catch
        {
            return new[] { new AudioDeviceItem("", Resources.SystemDefault) };
        }
    }

    // ── Music Library Folders ────────────────────────────────

    public ObservableCollection<string> MusicFolders { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    private async Task LoadMusicFoldersAsync()
    {
        var folders = await _library.GetWatchedFoldersAsync();
        Dispatcher.UIThread.Post(() =>
        {
            MusicFolders.Clear();
            foreach (var f in folders)
                MusicFolders.Add(f);
        });
    }

    public async Task AddFolderAsync(Window parentWindow)
    {
        var folders = await parentWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = Resources.SelectMusicFolder,
                AllowMultiple = false,
            });

        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        StatusMessage = string.Format(Resources.ScanningFmt, path);
        await _addLibraryFolder(path);
        MusicFolders.Add(path);
        StatusMessage = Resources.ScanComplete;
    }

    public async Task RemoveFolderAsync(string folder)
    {
        await _library.RemoveWatchedFolderAsync(folder);
        MusicFolders.Remove(folder);
    }

    public async Task ResetLibraryAsync()
    {
        StatusMessage = Resources.ResettingLibrary;
        await _library.ClearAsync();
        MusicFolders.Clear();
        await _onLibraryReset();
        StatusMessage = Resources.LibraryReset;
    }

    // ── Licenses ─────────────────────────────────────────────

    public ObservableCollection<LicenseEntry> Licenses { get; }

    private static IEnumerable<LicenseEntry> LoadLicenses()
    {
        // Scan LICENSES directory shipped alongside the assembly
        var coreDir = Path.Combine(AppContext.BaseDirectory, "LICENSES");

        // Also check relative to the source tree for development
        var searchDirs = new[]
        {
            coreDir,
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Orpheus.Desktop", "LICENSES"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<LicenseEntry>();

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.txt").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!seen.Add(name)) continue;

                try
                {
                    var text = File.ReadAllText(file);
                    var (project, url, body) = ParseLicenseHeaders(text);
                    entries.Add(new LicenseEntry(name, body, project, url));
                }
                catch
                {
                    // License-related fallback text is not localized per requirements
                    entries.Add(new LicenseEntry(name, "(Could not read license file)"));
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Parses optional "Project:" and "URL:" header lines from the top of a license file.
    /// Returns the parsed values and the remaining body text.
    /// </summary>
    private static (string? Project, string? Url, string Body) ParseLicenseHeaders(string text)
    {
        string? project = null;
        string? url = null;

        using var reader = new StringReader(text);
        var bodyStart = 0;
        var linesConsumed = 0;

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Project:", StringComparison.OrdinalIgnoreCase))
            {
                project = trimmed["Project:".Length..].Trim();
                linesConsumed++;
            }
            else if (trimmed.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
            {
                url = trimmed["URL:".Length..].Trim();
                linesConsumed++;
            }
            else if (string.IsNullOrWhiteSpace(line) && linesConsumed > 0)
            {
                // Skip blank line after headers
                linesConsumed++;
            }
            else
            {
                break;
            }

            bodyStart += line.Length + 1; // +1 for newline
        }

        var body = linesConsumed > 0 && bodyStart < text.Length
            ? text[bodyStart..]
            : text;

        return (project, url, body);
    }

    // ── INotifyPropertyChanged ───────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed record AudioDeviceItem(string Id, string Description)
{
    public override string ToString() => Description;
}

public sealed record LicenseEntry(string Name, string Text, string? Project = null, string? Url = null)
{
    public string DisplayName => Project ?? Name;
    public override string ToString() => DisplayName;
}

/// <summary>
/// Represents a language option for the settings Language selector.
/// </summary>
public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Represents a single palette color entry for editing in the settings UI.
/// </summary>
public sealed class PaletteColorEntry : INotifyPropertyChanged
{
    private string _hexValue;

    public PaletteColorEntry(string key, Avalonia.Media.Color color)
    {
        Key = key;
        _hexValue = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public string Key { get; }

    public string HexValue
    {
        get => _hexValue;
        set
        {
            if (_hexValue == value) return;
            _hexValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HexValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewBrush)));
        }
    }

    public Avalonia.Media.IBrush PreviewBrush
    {
        get
        {
            if (TryParseColor(out var color))
                return new Avalonia.Media.SolidColorBrush(color);
            return Avalonia.Media.Brushes.Transparent;
        }
    }

    public bool TryParseColor(out Avalonia.Media.Color color)
    {
        return Avalonia.Media.Color.TryParse(_hexValue, out color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

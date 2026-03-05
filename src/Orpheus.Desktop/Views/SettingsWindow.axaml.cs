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
using Avalonia.Input;
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

    public async void OnRescanLibrary(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RescanAllAsync();
    }

    public void OnApplyColors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ApplyCustomColors();
    }

    public void OnResetColors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.ResetCustomColors();
    }

    public void OnRebindClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not Button btn) return;
        if (btn.DataContext is not KeyBindingEntry entry) return;

        if (entry.IsRebinding)
        {
            ViewModel.CancelRebind();
        }
        else
        {
            ViewModel.BeginRebind(entry);
        }
    }

    public void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not Button btn) return;
        if (btn.DataContext is not KeyBindingEntry entry) return;

        ViewModel.CancelRebind();
        ViewModel.ClearBinding(entry);
    }

    public void OnRefreshAudioDevicesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.RefreshAudioDevices();
    }

    // ── Rebind capture via window-level Avalonia events ──────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) { base.OnKeyDown(e); return; }

        // Escape always cancels a rebind
        if (e.Key == Key.Escape)
        {
            vm.CancelRebind();
            e.Handled = true;
            return;
        }

        if (vm.RebindTarget is { } entry)
        {
            var keyName = MapAvaloniaKeyToSharpHook(e.Key);
            if (keyName is not null)
            {
                var combo = BuildComboString(e.KeyModifiers, keyName);
                vm.FinishRebind(combo);
            }
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.RebindTarget is not null)
        {
            var point = e.GetCurrentPoint(this);
            var mouseToken = MapPointerButtonToToken(point.Properties);

            if (mouseToken is not null)
            {
                var combo = BuildComboString(e.KeyModifiers, mouseToken);
                vm.FinishRebind(combo);
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.RebindTarget is not null)
        {
            var token = MapWheelDeltaToToken(e.Delta);
            if (token is not null)
            {
                var combo = BuildComboString(e.KeyModifiers, token);
                vm.FinishRebind(combo);
                e.Handled = true;
                return;
            }
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.CancelRebind();
        base.OnClosed(e);
    }

    // ── Mapping helpers (Avalonia → combo string tokens) ─────

    private static string? MapWheelDeltaToToken(Vector delta)
    {
        if (Math.Abs(delta.Y) >= Math.Abs(delta.X))
        {
            if (delta.Y > 0) return "WheelUp";
            if (delta.Y < 0) return "WheelDown";
        }
        else
        {
            if (delta.X > 0) return "WheelRight";
            if (delta.X < 0) return "WheelLeft";
        }
        return null;
    }

    private static string? MapPointerButtonToToken(PointerPointProperties props)
    {
        if (props.IsXButton1Pressed) return "Mouse4";
        if (props.IsXButton2Pressed) return "Mouse5";
        if (props.IsMiddleButtonPressed) return "Mouse3";
        if (props.IsLeftButtonPressed) return "Mouse1";
        if (props.IsRightButtonPressed) return "Mouse2";
        return null;
    }

    private static string BuildComboString(KeyModifiers modifiers, string keyName)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))     parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))   parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta))    parts.Add("Meta");
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static string? MapAvaloniaKeyToSharpHook(Key key) => key switch
    {
        // Function keys
        Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
        Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
        Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
        Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15",
        Key.F16 => "F16", Key.F17 => "F17", Key.F18 => "F18",
        Key.F19 => "F19", Key.F20 => "F20", Key.F21 => "F21",
        Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",

        // Letters
        Key.A => "A", Key.B => "B", Key.C => "C", Key.D => "D",
        Key.E => "E", Key.F => "F", Key.G => "G", Key.H => "H",
        Key.I => "I", Key.J => "J", Key.K => "K", Key.L => "L",
        Key.M => "M", Key.N => "N", Key.O => "O", Key.P => "P",
        Key.Q => "Q", Key.R => "R", Key.S => "S", Key.T => "T",
        Key.U => "U", Key.V => "V", Key.W => "W", Key.X => "X",
        Key.Y => "Y", Key.Z => "Z",

        // Numbers
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3",
        Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7",
        Key.D8 => "8", Key.D9 => "9",

        // Numpad digits
        Key.NumPad0 => "NumPad0", Key.NumPad1 => "NumPad1",
        Key.NumPad2 => "NumPad2", Key.NumPad3 => "NumPad3",
        Key.NumPad4 => "NumPad4", Key.NumPad5 => "NumPad5",
        Key.NumPad6 => "NumPad6", Key.NumPad7 => "NumPad7",
        Key.NumPad8 => "NumPad8", Key.NumPad9 => "NumPad9",

        // Numpad operators
        Key.Multiply => "NumPadMultiply",
        Key.Divide   => "NumPadDivide",
        Key.Subtract => "NumPadSubtract",
        Key.Add      => "NumPadAdd",
        Key.Decimal  => "NumPadDecimal",

        // Navigation
        Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
        Key.Home => "Home", Key.End => "End",
        Key.PageUp => "PageUp", Key.PageDown => "PageDown",
        Key.Insert => "Insert", Key.Delete => "Delete",

        // Common keys
        Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab",
        Key.Back => "Backspace", Key.Escape => "Escape",
        Key.Pause => "Pause", Key.Scroll => "ScrollLock",
        Key.PrintScreen => "PrintScreen",

        // Media keys
        Key.MediaPlayPause    => "MediaPlay",
        Key.MediaStop         => "MediaStop",
        Key.MediaNextTrack    => "MediaNext",
        Key.MediaPreviousTrack => "MediaPrevious",
        Key.VolumeUp          => "VolumeUp",
        Key.VolumeDown        => "VolumeDown",
        Key.VolumeMute        => "VolumeMute",

        // Punctuation/symbols
        Key.OemMinus => "Minus", Key.OemPlus => "Equals",
        Key.OemOpenBrackets => "OpenBracket", Key.OemCloseBrackets => "CloseBracket",
        Key.OemBackslash or Key.OemPipe => "Backslash", Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote", Key.OemComma => "Comma",
        Key.OemPeriod => "Period", Key.OemQuestion => "Slash",
        Key.OemTilde => "BackQuote",

        // Modifier-only keys — skip
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => null,

        _ => null,
    };

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
    private readonly AppState _state;
    private readonly PlayerController _controller;
    private readonly IMediaLibrary _library;
    private readonly Func<Task> _onLibraryReset;
    private readonly Func<Task> _onRescanAll;
    private readonly Func<string, Task> _addLibraryFolder;
    private readonly GlobalMediaKeyService? _mediaKeyService;

    private bool _enableTrayIcon;
    private string _selectedTheme;
    private string? _selectedVariant;
    private string? _selectedAudioDevice;
    private string _statusMessage = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        ThemeManager themeManager,
        AppConfig config,
        AppState state,
        PlayerController controller,
        IMediaLibrary library,
        Func<Task> onLibraryReset,
        Func<Task> onRescanAll,
        Func<string, Task> addLibraryFolder,
        GlobalMediaKeyService? mediaKeyService = null)
    {
        _themeManager = themeManager;
        _config = config;
        _state = state;
        _controller = controller;
        _library = library;
        _onLibraryReset = onLibraryReset;
        _onRescanAll = onRescanAll;
        _addLibraryFolder = addLibraryFolder;
        _mediaKeyService = mediaKeyService;

        _selectedTheme = _themeManager.ActiveLayout ?? ThemeManager.DefaultLayout;
        _selectedVariant = _themeManager.ActiveVariant ?? Resources.Default;

        Themes = new ObservableCollection<string>(_themeManager.GetAvailableLayouts());
        Variants = new ObservableCollection<string>(GetVariantsWithDefault());
        AudioDevices = new ObservableCollection<AudioDeviceItem>(GetAudioDevices());
        MusicFolders = new ObservableCollection<string>();

        _controller.AudioDevicesChanged += OnAudioDevicesChanged;
        Licenses = new ObservableCollection<LicenseEntry>(LoadLicenses());

        SelectedAudioDevice = _state.AudioDevice ?? "";
        _enableTrayIcon = _config.EnableTrayIcon;

        // Language selector
        LanguageOptions = new ObservableCollection<LanguageOption>(GetLanguageOptions());
        _selectedLanguage = LanguageOptions.FirstOrDefault(l => l.Code == (Resources.Culture?.Name ?? "en"))
                            ?? LanguageOptions.First();

        // Key bindings — initialize from config
        KeyBindings = new ObservableCollection<KeyBindingEntry>
        {
            new("play.svg",     _config.KeyPlayPause,     b => { _config.KeyPlayPause = b; SaveAndApplyBindings(); }),
            new("next.svg",     _config.KeyNextTrack,      b => { _config.KeyNextTrack = b; SaveAndApplyBindings(); }),
            new("previous.svg", _config.KeyPreviousTrack,  b => { _config.KeyPreviousTrack = b; SaveAndApplyBindings(); }),
            new("stop.svg",     _config.KeyStop,           b => { _config.KeyStop = b; SaveAndApplyBindings(); }),
            new("volume-high.svg", _config.KeyVolumeUp,     b => { _config.KeyVolumeUp = b; SaveAndApplyBindings(); }),
            new("volume-low.svg",  _config.KeyVolumeDown,   b => { _config.KeyVolumeDown = b; SaveAndApplyBindings(); }),
        };

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
    public string LocRescanLibrary => Resources.RescanLibrary;
    public string LocOutput => Resources.Output;
    public string LocAudioOutput => Resources.AudioOutput;
    public string LocOutputDevice => Resources.OutputDevice;
    public string LocLicenses => Resources.Licenses;
    public string LocOpenSourceLicenses => Resources.OpenSourceLicenses;

    private static readonly string[] _locPropertyNames =
    [
        nameof(LocSettings), nameof(LocGeneral), nameof(LocEnableTrayIcon),
        nameof(LocLanguage), nameof(LocTheme), nameof(LocAppearance),
        nameof(LocColorVariant), nameof(LocCustomColors),
        nameof(LocEnableCustomColorOverrides), nameof(LocApplyColors),
        nameof(LocResetToDefault), nameof(LocLayoutsDirectory),
        nameof(LocLibrary), nameof(LocMusicFolders), nameof(LocAddFolder),
        nameof(LocRemoveSelected), nameof(LocDatabase),
        nameof(LocResetLibraryDescription), nameof(LocResetLibrary), nameof(LocRescanLibrary),
        nameof(LocOutput), nameof(LocAudioOutput), nameof(LocOutputDevice),
        nameof(LocLicenses), nameof(LocOpenSourceLicenses),
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

    // ── Key Bindings ─────────────────────────────────────────

    /// <summary>
    /// The rebindable shortcut entries, displayed with action icons.
    /// </summary>
    public ObservableCollection<KeyBindingEntry> KeyBindings { get; }

    /// <summary>Keyboard icon for the key bindings section header.</summary>
    public IImage? KeyboardIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/keyboard.svg", ResolveIconColor());

    /// <summary>Refresh icon for audio devices.</summary>
    public IImage? RefreshIcon =>
        SvgIconHelper.Load("avares://Orpheus.Desktop/assets/icons/repeat-none.svg", ResolveIconColor());

    private static Color ResolveIconColor()
    {
        if (Application.Current!.Resources.TryGetResource(
                "IconColor", Application.Current.ActualThemeVariant, out var obj)
            && obj is Color color)
            return color;
        return Color.Parse("#D4A843");
    }

    /// <summary>
    /// The entry currently being rebound (waiting for input), or null.
    /// Exposed so the SettingsWindow code-behind can check it in event overrides.
    /// </summary>
    public KeyBindingEntry? RebindTarget { get; private set; }

    /// <summary>Raised when shortcuts change so the App can reconfigure the hook.</summary>
    public event Action<AppConfig>? ShortcutsChanged;

    /// <summary>
    /// Raised when the rebind listening state changes so the App can suppress/restore
    /// hotkey matching in the <see cref="GlobalMediaKeyService"/>.
    /// </summary>
    public event Action<bool>? ShortcutListeningChanged;

    /// <summary>
    /// Start listening for input to rebind the given entry.
    /// </summary>
    public void BeginRebind(KeyBindingEntry entry)
    {
        // Cancel any previous rebind
        if (RebindTarget is not null)
        {
            RebindTarget.IsRebinding = false;
            RebindTarget.KeyDisplay = GlobalMediaKeyService.FormatCombo(RebindTarget.ComboString);
        }

        RebindTarget = entry;
        entry.IsRebinding = true;
        entry.KeyDisplay = "...";

        // Suppress global hotkeys while rebinding
        ShortcutListeningChanged?.Invoke(true);
    }

    /// <summary>
    /// Complete the rebind with the given combo string (e.g. "Ctrl+Mouse4").
    /// Called by the SettingsWindow code-behind after capturing input.
    /// </summary>
    public void FinishRebind(string combo)
    {
        if (RebindTarget is null) return;

        RebindTarget.ComboString = combo; // setter updates KeyDisplay
        RebindTarget.IsRebinding = false;
        RebindTarget.OnComboChanged(combo);

        RebindTarget = null;
        ShortcutListeningChanged?.Invoke(false);
    }

    /// <summary>
    /// Cancel the current rebind operation without changing the binding.
    /// </summary>
    public void CancelRebind()
    {
        if (RebindTarget is not null)
        {
            RebindTarget.IsRebinding = false;
            RebindTarget.KeyDisplay = GlobalMediaKeyService.FormatCombo(RebindTarget.ComboString);
            RebindTarget = null;
        }

        ShortcutListeningChanged?.Invoke(false);
    }

    /// <summary>
    /// Clear the binding for the given entry (set to empty string = use default).
    /// </summary>
    public void ClearBinding(KeyBindingEntry entry)
    {
        entry.ComboString = ""; // setter updates KeyDisplay to "(None)"
        entry.OnComboChanged("");
    }

    private void SaveAndApplyBindings()
    {
        _config.Save();
        _mediaKeyService?.Configure(
            _config.KeyPlayPause,
            _config.KeyNextTrack,
            _config.KeyPreviousTrack,
            _config.KeyStop,
            _config.KeyVolumeUp,
            _config.KeyVolumeDown);
        ShortcutsChanged?.Invoke(_config);
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
            new LanguageOption("bg", "Български"),
            new LanguageOption("tr", "Türkçe"),
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
            _controller.SetAudioDevice(value);
            _state.AudioDevice = string.IsNullOrEmpty(value) ? null : value;
            _state.Save();
            _config.Save();
        }
    }

    private IEnumerable<AudioDeviceItem> GetAudioDevices()
    {
        var devices = _controller.GetAudioDevices();
        if (devices.Count > 0)
        {
            return devices.Select(d => new AudioDeviceItem(d.Id ?? "", d.Description));
        }
        return new[] { new AudioDeviceItem("", Resources.SystemDefault) };
    }

    private void OnAudioDevicesChanged(object? sender, IReadOnlyList<(string? Id, string Description)> devices)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var savedDevice = _selectedAudioDevice;
            AudioDevices.Clear();
            foreach (var d in devices)
            {
                AudioDevices.Add(new AudioDeviceItem(d.Id ?? "", d.Description));
            }
            _selectedAudioDevice = savedDevice;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioDevice)));
        });
    }

    public void RefreshAudioDevices()
    {
        _controller.RefreshAudioDevices();
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
        await _onLibraryReset();
        StatusMessage = Resources.LibraryReset;
    }

    public async Task RescanAllAsync()
    {
        StatusMessage = Resources.ScanningLibrary;
        await _onRescanAll();
        StatusMessage = Resources.ScanComplete;
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

/// <summary>
/// Represents a single rebindable shortcut row in the settings UI.
/// Displays an action icon (SVG), the current combo display, a rebind button, and a clear button.
/// Supports keyboard keys, mouse buttons, scroll wheel, and modifier combinations.
/// </summary>
public sealed class KeyBindingEntry : INotifyPropertyChanged
{
    private string _comboString;
    private string _keyDisplay;
    private bool _isRebinding;
    private readonly Action<string> _onChanged;

    public KeyBindingEntry(string iconFileName, string comboString, Action<string> onChanged, string? badge = null)
    {
        IconPath = $"avares://Orpheus.Desktop/assets/icons/{iconFileName}";
        _comboString = comboString;
        _keyDisplay = GlobalMediaKeyService.FormatCombo(comboString);
        _onChanged = onChanged;
        Badge = badge;
    }

    /// <summary>avares:// path to the action icon SVG.</summary>
    public string IconPath { get; }

    /// <summary>Optional short badge label overlaid on the icon (e.g. "+" / "−").</summary>
    public string? Badge { get; }

    /// <summary>True when <see cref="Badge"/> is not null or empty.</summary>
    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>The action icon as an IImage for display in the settings UI.</summary>
    public IImage? ActionIcon => SvgIconHelper.Load(IconPath, ResolveIconColor());

    private static Color ResolveIconColor()
    {
        if (Application.Current!.Resources.TryGetResource(
                "IconColor", Application.Current.ActualThemeVariant, out var obj)
            && obj is Color color)
            return color;
        return Color.Parse("#D4A843");
    }

    /// <summary>The raw combo string stored in config (e.g. "Ctrl+WheelUp", "Mouse4", "MediaPlay").</summary>
    public string ComboString
    {
        get => _comboString;
        set
        {
            if (_comboString == value) return;
            _comboString = value;
            KeyDisplay = GlobalMediaKeyService.FormatCombo(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComboString)));
        }
    }

    /// <summary>Human-readable label for the currently assigned binding.</summary>
    public string KeyDisplay
    {
        get => _keyDisplay;
        set
        {
            if (_keyDisplay == value) return;
            _keyDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyDisplay)));
        }
    }

    /// <summary>True while waiting for the user to press a new key, mouse button, or scroll wheel.</summary>
    public bool IsRebinding
    {
        get => _isRebinding;
        set
        {
            if (_isRebinding == value) return;
            _isRebinding = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRebinding)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RebindButtonText)));
        }
    }

    /// <summary>Button text: "..." while rebinding, otherwise a keyboard glyph.</summary>
    public string RebindButtonText => _isRebinding ? "..." : "\u2328";

    /// <summary>Called by the SettingsViewModel when a combo is captured or cleared.</summary>
    internal void OnComboChanged(string combo) => _onChanged(combo);

    public event PropertyChangedEventHandler? PropertyChanged;
}

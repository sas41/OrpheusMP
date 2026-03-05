using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Orpheus.Desktop.Lang;
using Orpheus.Desktop.Theming;

namespace Orpheus.Desktop;

public partial class App : Application
{
    public static event Action? LanguageChanged;

    public ThemeManager? ThemeManager { get; private set; }
    public AppConfig Config { get; private set; } = new();
    public AppState State { get; private set; } = new();

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _showMenuItem;
    private NativeMenuItem? _quitMenuItem;
    private bool _isQuitting;

    /// <summary>
    /// True when the tray icon is enabled and the window should hide on close
    /// instead of quitting the application.
    /// </summary>
    public bool IsTrayIconActive => _trayIcon?.IsVisible == true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Load user settings and session state (creates defaults if missing)
        Config = AppConfig.Load();
        State = AppState.Load();

        // Initialize localization from config (must happen before UI is built)
        if (!string.IsNullOrEmpty(Config.Language))
            Lang.Resources.Culture = new CultureInfo(Config.Language);

        // Initialize theming from config
        ThemeManager = new ThemeManager(this);
        ThemeManager.Apply(Config.Theme, Config.Variant);

        // Persist so first-run users get files on disk
        Config.Save();
        State.Save();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            RestoreWindowGeometry(mainWindow);

            // Wire up close-to-tray behavior
            mainWindow.Closing += OnMainWindowClosing;

            // Create tray icon if enabled in config
            if (Config.EnableTrayIcon)
                CreateTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Called from SettingsViewModel when the user toggles the tray icon setting.
    /// </summary>
    public void SetTrayIconEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_trayIcon is null)
                CreateTrayIcon();
            else
                _trayIcon.IsVisible = true;
        }
        else
        {
            if (_trayIcon is not null)
                _trayIcon.IsVisible = false;
        }
    }

    public static void SetLanguage(string cultureCode)
    {
        Lang.Resources.Culture = new CultureInfo(cultureCode);
        LanguageChanged?.Invoke();
    }

    private void CreateTrayIcon()
    {
        _showMenuItem = new NativeMenuItem(Lang.Resources.ShowOrpheus);
        _showMenuItem.Click += (_, _) => ShowMainWindow();

        _quitMenuItem = new NativeMenuItem(Lang.Resources.Quit);
        _quitMenuItem.Click += (_, _) => QuitApplication();

        var menu = new NativeMenu();
        menu.Items.Add(_showMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_quitMenuItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = Lang.Resources.AppTitle,
            Menu = menu,
            IsVisible = true,
            Icon = new WindowIcon(
                AssetLoader.Open(new Uri("avares://Orpheus.Desktop/assets/icon-256.png"))),
        };

        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        LanguageChanged += OnLanguageChangedTray;
    }

    private void OnLanguageChangedTray()
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = Lang.Resources.AppTitle;
        if (_showMenuItem is not null)
            _showMenuItem.Header = Lang.Resources.ShowOrpheus;
        if (_quitMenuItem is not null)
            _quitMenuItem.Header = Lang.Resources.Quit;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window)
            return;

        // Always save window geometry when closing / hiding
        SaveWindowGeometry(window);

        if (_isQuitting || !IsTrayIconActive)
        {
            // Actually shutting down — let the close proceed
            return;
        }

        // Hide to tray instead of closing
        e.Cancel = true;
        window.Hide();
    }

    /// <summary>
    /// Restore the window position and size from <see cref="State"/>.
    /// On a fresh install (no saved geometry), center on the primary monitor.
    /// </summary>
    private void RestoreWindowGeometry(Window window)
    {
        var state = State;

        if (state.WindowWidth.HasValue && state.WindowHeight.HasValue)
        {
            window.Width = state.WindowWidth.Value;
            window.Height = state.WindowHeight.Value;
        }

        if (state.WindowX.HasValue && state.WindowY.HasValue)
        {
            window.Position = new PixelPoint(
                (int)state.WindowX.Value,
                (int)state.WindowY.Value);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            // Fresh launch — center on primary monitor
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (state.WindowMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Save the current window position and size to <see cref="State"/>.
    /// We record the normal (non-maximized) bounds so that restoring from
    /// maximized state puts the window back in a sensible position.
    /// </summary>
    private void SaveWindowGeometry(Window window)
    {
        var state = State;
        state.WindowMaximized = window.WindowState == WindowState.Maximized;

        // Only save position/size when not maximized — the maximized
        // geometry is the full screen and not useful for restore.
        if (window.WindowState == WindowState.Normal)
        {
            state.WindowX = window.Position.X;
            state.WindowY = window.Position.Y;
            state.WindowWidth = window.Bounds.Width;
            state.WindowHeight = window.Bounds.Height;
        }

        state.Save();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window is not null)
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            }
        }
    }

    private void QuitApplication()
    {
        _isQuitting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}

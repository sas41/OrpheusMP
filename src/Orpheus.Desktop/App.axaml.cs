using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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
    private CancellationTokenSource? _ipcCts;

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
            // Resolve any file path passed as a launch argument (e.g. from
            // "Open With" or a file-association double-click). The desktop
            // passes a file:// URI on Linux/macOS and a plain path on Windows.
            var launchPath = ResolveFileLaunchArg(desktop.Args);

            var mainWindow = new MainWindow(launchPath);
            desktop.MainWindow = mainWindow;

            RestoreWindowGeometry(mainWindow);

            // Wire up close-to-tray behavior
            mainWindow.Closing += OnMainWindowClosing;

            // Create tray icon if enabled in config
            if (Config.EnableTrayIcon)
                CreateTrayIcon();

            UpdateShutdownMode();

            // Start the IPC listener so secondary instances can forward file
            // paths to this (primary) instance instead of opening a new window.
            StartIpcServer(mainWindow);
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

        UpdateShutdownMode();
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

        // Allow OS-initiated shutdown (e.g. system logout/restart/shutdown)
        // to close the application instead of hiding it to the tray.
        if (e.CloseReason == WindowCloseReason.OSShutdown)
        {
            QuitApplication();
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

    /// <summary>
    /// Starts a background named-pipe server that accepts connections from
    /// secondary instances and routes file paths (or empty "activate" tokens)
    /// to the main window's ViewModel.
    /// </summary>
    private void StartIpcServer(MainWindow mainWindow)
    {
        _ipcCts = new CancellationTokenSource();
        var ct = _ipcCts.Token;

        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName:        Program.PipeName,
                        direction:       PipeDirection.In,
                        maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options:         PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var ms = new MemoryStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await server.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        ms.Write(buffer, 0, read);

                    var filePath = Encoding.UTF8.GetString(ms.ToArray());

                    // Dispatch to the UI thread
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ShowMainWindow();

                        if (!string.IsNullOrWhiteSpace(filePath) &&
                            mainWindow.DataContext is MainWindowViewModel vm)
                        {
                            await vm.AddFileToQueueAndPlayAsync(filePath).ConfigureAwait(false);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient pipe errors — just restart the loop
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }, ct);
    }

    private void QuitApplication()
    {
        _isQuitting = true;
        _ipcCts?.Cancel();
        _ipcCts?.Dispose();
        _ipcCts = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void UpdateShutdownMode()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = IsTrayIconActive
            ? ShutdownMode.OnExplicitShutdown
            : ShutdownMode.OnMainWindowClose;
    }

    /// <summary>
    /// Extracts a local file path from the launch arguments, if any.
    /// Handles both plain paths (Windows/macOS) and file:// URIs (Linux desktops).
    /// Returns null if no valid file argument is present.
    /// </summary>
    private static string? ResolveFileLaunchArg(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
            return null;

        // Take the first non-option argument
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
                continue;

            // file:// URI (e.g. from xdg-open on Linux)
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            // Plain path
            if (File.Exists(arg))
                return arg;
        }

        return null;
    }
}

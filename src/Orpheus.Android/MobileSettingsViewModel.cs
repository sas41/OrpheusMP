using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Orpheus.Android;

/// <summary>
/// ViewModel for the mobile Settings screen.
/// Covers: Library folder management + color variant selection.
/// </summary>
public sealed class MobileSettingsViewModel : INotifyPropertyChanged
{
    private readonly MobileViewModel _mainVm;

    private string _statusMessage = "";
    private string _selectedVariant;
    private string _selectedDisplayMode;
    private bool _isScanning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MobileSettingsViewModel(MobileViewModel mainVm)
    {
        _mainVm = mainVm;

        MusicFolders = new ObservableCollection<string>(mainVm.WatchedFolders);
        // Keep in sync if the main VM's folder list changes
        mainVm.WatchedFolders.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MusicFolders.Clear();
                foreach (var f in mainVm.WatchedFolders)
                    MusicFolders.Add(f);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoFolders)));
            });
        };
        MusicFolders.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoFolders)));

        Variants = new ObservableCollection<string>(App.AvailableVariants);

        var app = App.Current as App;
        var active = app?.ActiveVariant;
        _selectedVariant = string.IsNullOrEmpty(active) ? "Default" : active;

        DisplayModes = new ObservableCollection<string>
        {
            "Title + Album",
            "File Name + Folder",
            "Title + Album → File Name + Folder",
        };
        _selectedDisplayMode = mainVm.TrackDisplayMode switch
        {
            QueueDisplayMode.FileNameFolder         => "File Name + Folder",
            QueueDisplayMode.TitleAlbumWithFallback => "Title + Album → File Name + Folder",
            _                                       => "Title + Album",
        };
    }

    // ── Library ───────────────────────────────────────────────────────

    public ObservableCollection<string> MusicFolders { get; }
    public bool HasNoFolders => MusicFolders.Count == 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetField(ref _isScanning, value);
    }

    public async Task AddFolderAsync(string path)
    {
        IsScanning = true;
        StatusMessage = "Scanning…";
        try
        {
            await _mainVm.AddFolderAsync(path);
            StatusMessage = "Scan complete.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task RemoveFolderAsync(string path)
    {
        await _mainVm.RemoveFolderAsync(path);
        StatusMessage = "";
    }

    public async Task RescanAsync()
    {
        IsScanning = true;
        StatusMessage = "Rescanning…";
        try
        {
            await _mainVm.RescanAsync();
            StatusMessage = "Scan complete.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task ResetLibraryAsync()
    {
        StatusMessage = "Resetting library…";
        try
        {
            await _mainVm.ResetLibraryAsync();
            StatusMessage = "Library reset.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    // ── Appearance ────────────────────────────────────────────────────

    public ObservableCollection<string> Variants { get; }

    public string SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (!SetField(ref _selectedVariant, value)) return;
            (App.Current as App)?.ApplyVariant(value);
        }
    }

    public ObservableCollection<string> DisplayModes { get; }

    public string SelectedDisplayMode
    {
        get => _selectedDisplayMode;
        set
        {
            if (!SetField(ref _selectedDisplayMode, value)) return;
            _mainVm.TrackDisplayMode = value switch
            {
                "File Name + Folder"                    => QueueDisplayMode.FileNameFolder,
                "Title + Album → File Name + Folder"    => QueueDisplayMode.TitleAlbumWithFallback,
                _                                       => QueueDisplayMode.TitleAlbum,
            };
        }
    }

    // ── Licenses ─────────────────────────────────────────────────────

    public ObservableCollection<MobileLicenseEntry> Licenses { get; } =
        new(LoadLicenses());

    private static IEnumerable<MobileLicenseEntry> LoadLicenses()
    {
        var searchDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "LICENSES"),
            // Dev build: walk up from the Android project output to the Desktop LICENSES folder
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Orpheus.Desktop", "LICENSES"),
        };

        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<MobileLicenseEntry>();

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
                    entries.Add(new MobileLicenseEntry(name, body, project, url));
                }
                catch
                {
                    entries.Add(new MobileLicenseEntry(name, "(Could not read license file)"));
                }
            }
        }

        return entries;
    }

    private static (string? Project, string? Url, string Body) ParseLicenseHeaders(string text)
    {
        string? project = null;
        string? url     = null;

        using var reader = new StringReader(text);
        var bodyStart    = 0;
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
                linesConsumed++;
            }
            else
            {
                break;
            }
            bodyStart += line.Length + 1;
        }

        var body = linesConsumed > 0 && bodyStart < text.Length
            ? text[bodyStart..]
            : text;

        return (project, url, body);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class MobileLicenseEntry : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string  Name        { get; }
    public string  Text        { get; }
    public string? Project     { get; }
    public string? Url         { get; }
    public string  DisplayName => Project ?? Name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
        }
    }

    public bool IsCollapsed => !_isExpanded;

    public MobileLicenseEntry(string name, string text, string? project = null, string? url = null)
    {
        Name    = name;
        Text    = text;
        Project = project;
        Url     = url;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

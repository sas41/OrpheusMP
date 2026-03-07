using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using Orpheus.Core.Library;

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

        MusicFolders = new ObservableCollection<MobileFolderScanStatus>(
            mainVm.WatchedFolders.Select(f => new MobileFolderScanStatus(f)));
        // Keep in sync if the main VM's folder list changes
        mainVm.WatchedFolders.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var existing = MusicFolders.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
                // Remove entries no longer in watched folders
                var watchedSet = new HashSet<string>(mainVm.WatchedFolders, StringComparer.OrdinalIgnoreCase);
                for (var i = MusicFolders.Count - 1; i >= 0; i--)
                {
                    if (!watchedSet.Contains(MusicFolders[i].Path))
                        MusicFolders.RemoveAt(i);
                }
                // Add new entries
                foreach (var f in mainVm.WatchedFolders)
                {
                    if (!existing.ContainsKey(f))
                        MusicFolders.Add(new MobileFolderScanStatus(f));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoFolders)));
            });
        };
        MusicFolders.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoFolders)));

        // Subscribe to scan progress so folder rows update live
        mainVm.ScannerProgress  += OnScannerProgress;
        mainVm.MetadataProgress += OnMetadataProgress;

        // Seed progress bars from the DB for folders already scanned in the past
        _ = SeedFolderStatsAsync();

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

    public ObservableCollection<MobileFolderScanStatus> MusicFolders { get; }
    public bool HasNoFolders => MusicFolders.Count == 0;

    public void Dispose()
    {
        _mainVm.ScannerProgress  -= OnScannerProgress;
        _mainVm.MetadataProgress -= OnMetadataProgress;
    }

    private async Task SeedFolderStatsAsync()
    {
        try
        {
            var stats = await _mainVm.GetFolderStatsAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (folderPath, s) in stats)
                {
                    if (s.Total == 0) continue;

                    var entry = MusicFolders.FirstOrDefault(f =>
                        string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;   // folder no longer watched

                    // Only seed when live scan events haven't already populated higher values
                    if (entry.FsScanTotal == 0)
                    {
                        entry.FsScanTotal = s.Total;
                        entry.FsScanDone  = s.Total;   // files were all indexed in a previous scan
                    }
                    if (entry.MetaTotal == 0)
                    {
                        entry.MetaTotal = s.Total;
                        entry.MetaDone  = s.Total - s.Pending;
                    }
                }
            });
        }
        catch { /* non-critical — UI just stays at 0/0 */ }
    }

    private void OnScannerProgress(object? sender, LibraryScanProgress e)
    {
        if (e.FolderPath is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var entry = MusicFolders.FirstOrDefault(f =>
                string.Equals(f.Path, e.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new MobileFolderScanStatus(e.FolderPath);
                MusicFolders.Add(entry);
            }
            entry.FsScanTotal = Math.Max(entry.FsScanTotal, e.TotalFiles);
            entry.FsScanDone  = e.IsComplete ? entry.FsScanTotal : Math.Min(e.TotalFiles, entry.FsScanTotal);
            entry.IsFsScanning = !e.IsComplete;
        });
    }

    private void OnMetadataProgress(object? sender, LibraryScanProgress e)
    {
        if (e.FolderPath is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var entry = MusicFolders.FirstOrDefault(f =>
                string.Equals(f.Path, e.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new MobileFolderScanStatus(e.FolderPath);
                MusicFolders.Add(entry);
            }
            entry.MetaTotal = Math.Max(entry.MetaTotal, e.TotalFiles);
            entry.MetaDone  = e.IsComplete ? entry.MetaTotal : e.ProcessedFiles;
            entry.IsMetaScanning = !e.IsComplete;
        });
    }

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

    public string BuildString => GetBuildString();

    public bool HasBuildString => !string.IsNullOrWhiteSpace(BuildString);

    private static string GetBuildString()
        => AppendBuildTime(GetBuildLabel(), GetBuildTimeUtc());

    private static string? GetBuildTimeUtc()
        => typeof(MobileSettingsViewModel)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => string.Equals(attr.Key, "BuildTimeUtc", StringComparison.Ordinal))
            ?.Value;

    private static string AppendBuildTime(string baseText, string? buildTime)
        => string.IsNullOrWhiteSpace(buildTime) ? baseText : $"{baseText} - {buildTime}";

    private static string GetBuildLabel()
    {
#if DEBUG
        return "Debug build";
#else
        return "Version";
#endif
    }

    public async Task AddFolderAsync(string path)
    {
        IsScanning = true;
        StatusMessage = "Scanning…";
        // Pre-add the entry so progress bars appear immediately
        if (!MusicFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            MusicFolders.Add(new MobileFolderScanStatus(path));
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
        var entries = new List<MobileLicenseEntry>();

        // Primary: load from Avalonia resources (embedded in the APK at build time)
        try
        {
            var assets = AssetLoader.GetAssets(new Uri("avares://Orpheus.Android/LICENSES/"), null);
            foreach (var uri in assets.OrderBy(u => u.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                try
                {
                    using var stream = AssetLoader.Open(uri);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    var (project, url, body) = ParseLicenseHeaders(text);
                    entries.Add(new MobileLicenseEntry(name, body, project, url));
                }
                catch
                {
                    entries.Add(new MobileLicenseEntry(name, "(Could not read license file)"));
                }
            }
        }
        catch
        {
            // AssetLoader not available or no assets — fall back to filesystem
        }

        if (entries.Count > 0)
            return entries;

        // Fallback: filesystem (development / desktop preview)
        var searchDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "LICENSES"),
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Orpheus.Desktop", "LICENSES"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

/// <summary>
/// Observable scan-progress model for a single watched folder row in the mobile Settings screen.
/// Mirrors the desktop FolderScanStatus, tracking filesystem-scan and metadata-scan progress.
/// </summary>
public sealed class MobileFolderScanStatus : INotifyPropertyChanged
{
    private int  _fsScanTotal;
    private int  _fsScanDone;
    private bool _isFsScanning;
    private int  _metaTotal;
    private int  _metaDone;
    private bool _isMetaScanning;

    public MobileFolderScanStatus(string path) { Path = path; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    // ── Filesystem scan ──────────────────────────────────────────

    public int FsScanTotal
    {
        get => _fsScanTotal;
        set { if (_fsScanTotal == value) return; _fsScanTotal = value; Notify(); Notify(nameof(FsScanProgress)); Notify(nameof(FsScanIsIndeterminate)); }
    }

    public int FsScanDone
    {
        get => _fsScanDone;
        set { if (_fsScanDone == value) return; _fsScanDone = value; Notify(); Notify(nameof(FsScanProgress)); }
    }

    public bool IsFsScanning
    {
        get => _isFsScanning;
        set { if (_isFsScanning == value) return; _isFsScanning = value; Notify(); Notify(nameof(FsScanIsIndeterminate)); }
    }

    public double FsScanProgress => _fsScanTotal > 0 ? Math.Min(100.0, _fsScanDone * 100.0 / _fsScanTotal) : 0;
    public bool   FsScanIsIndeterminate => _isFsScanning && _fsScanTotal == 0;

    // ── Metadata scan ────────────────────────────────────────────

    public int MetaTotal
    {
        get => _metaTotal;
        set { if (_metaTotal == value) return; _metaTotal = value; Notify(); Notify(nameof(MetaProgress)); Notify(nameof(MetaIsIndeterminate)); }
    }

    public int MetaDone
    {
        get => _metaDone;
        set { if (_metaDone == value) return; _metaDone = value; Notify(); Notify(nameof(MetaProgress)); }
    }

    public bool IsMetaScanning
    {
        get => _isMetaScanning;
        set { if (_isMetaScanning == value) return; _isMetaScanning = value; Notify(); Notify(nameof(MetaIsIndeterminate)); }
    }

    public double MetaProgress => _metaTotal > 0 ? Math.Min(100.0, _metaDone * 100.0 / _metaTotal) : 0;
    public bool   MetaIsIndeterminate => _isMetaScanning && _metaTotal == 0;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    // ── INotifyPropertyChanged ────────────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Orpheus.Core.Playback;

namespace Orpheus.Android;

[Activity(
    Label = "Orpheus",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int StoragePermissionRequestCode = 1001;

    // The permission we need depends on API level:
    //   API 33+ (Android 13) → READ_MEDIA_AUDIO
    //   API 26–32            → READ_EXTERNAL_STORAGE
    private static string RequiredPermission =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? Manifest.Permission.ReadMediaAudio
            : Manifest.Permission.ReadExternalStorage;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
               .WithInterFont()
               .LogToTrace();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestStoragePermissionIfNeeded();
        StartPlaybackService();

        // Handle a file opened on cold start
        if (Intent is not null)
            _ = HandleOpenIntentAsync(Intent);
    }

    protected override void OnDestroy()
    {
        // Stop the foreground service when the activity is fully destroyed
        // (i.e. the user swiped the app away from the recents list).
        // While the activity is merely backgrounded the service keeps running.
        if (IsFinishing)
            StopService(new Intent(this, typeof(PlaybackService)));

        base.OnDestroy();
    }

    private void StartPlaybackService()
    {
        var intent = new Intent(this, typeof(PlaybackService));
        StartForegroundService(intent);
    }

    // Called when the app is already running (SingleTop) and another
    // "Open With" intent arrives.
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null)
            _ = HandleOpenIntentAsync(intent);
    }

    private async Task HandleOpenIntentAsync(Intent intent)
    {
        if (intent.Action != Intent.ActionView || intent.Data is null)
            return;

        var filePath = await ResolveIntentPathAsync(intent);
        if (filePath is null)
            return;

        // Wait for the ViewModel to be ready (it's created during
        // OnFrameworkInitializationCompleted which runs inside base.OnCreate).
        var vm = await WaitForViewModelAsync();
        if (vm is not null)
            await vm.PlayFileAsync(filePath);
    }

    /// <summary>
    /// Resolves the intent URI to a local file path.
    /// For content:// URIs, the stream is copied to a temp file because
    /// VLC requires a real filesystem path.
    /// </summary>
    private async Task<string?> ResolveIntentPathAsync(Intent intent)
    {
        var uri = intent.Data!;

        if (uri.Scheme == "file")
            return uri.Path;

        if (uri.Scheme == "content")
        {
            try
            {
                // Derive a filename from the content URI display name
                var fileName = uri.LastPathSegment ?? "orpheus_temp";
                var ext = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) ext = ".audio";

                var tempPath = Path.Combine(CacheDir!.AbsolutePath, $"open_{Path.GetFileNameWithoutExtension(fileName)}{ext}");

                using var input = ContentResolver!.OpenInputStream(uri);
                if (input is null) return null;

                using var output = File.Create(tempPath);
                await input.CopyToAsync(output);

                return tempPath;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Polls briefly for the App.ViewModel to become available after Avalonia
    /// finishes its initialization inside OnCreate.
    /// </summary>
    private static async Task<MobileViewModel?> WaitForViewModelAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var vm = (Avalonia.Application.Current as App)?.ViewModel;
            if (vm is not null) return vm;
            await Task.Delay(50);
        }
        return (Avalonia.Application.Current as App)?.ViewModel;
    }

    private void RequestStoragePermissionIfNeeded()
    {
        var permission = RequiredPermission;
        if (CheckSelfPermission(permission) == Permission.Granted)
        {
            // Already granted (e.g. returning from background) — notify the VM.
            NotifyPermissionGranted();
            return;
        }

        // Show the system permission dialog.
        RequestPermissions([permission], StoragePermissionRequestCode);
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != StoragePermissionRequestCode) return;

        // If at least one result is Granted, proceed with the scan.
        if (grantResults.Any(r => r == Permission.Granted))
            NotifyPermissionGranted();
        // If denied we stay silent — the library will be empty and the user can
        // still add folders manually via the folder picker (which uses SAF and
        // does not need READ_EXTERNAL_STORAGE on API 29+).
    }

    private static void NotifyPermissionGranted()
    {
        var vm = (Avalonia.Application.Current as App)?.ViewModel;
        if (vm is not null)
            _ = vm.GrantStoragePermissionAsync();
    }
}

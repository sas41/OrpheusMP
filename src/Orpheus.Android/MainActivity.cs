using System.Linq;
using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace Orpheus.Android;

[Activity(
    Label = "Orpheus",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
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

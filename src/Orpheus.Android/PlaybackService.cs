using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Views;
using Orpheus.Core.Metadata;

// Alias the Android type to avoid collision with Orpheus.Core.Playback.PlaybackState
using AndroidPlaybackState = Android.Media.Session.PlaybackState;

namespace Orpheus.Android;

/// <summary>
/// Android foreground service that:
///   • keeps audio alive when the app is backgrounded or the screen locks
///   • hosts a <see cref="MediaSession"/> so the system shows transport controls
///     on the lock screen and in the notification tray
///   • routes media-key events (headset buttons, Bluetooth, keyboards) back
///     to the app's <see cref="MobileViewModel"/>
/// </summary>
[Service(
    Name = "net.orpheusmp.android.PlaybackService",
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback,
    Exported = false)]
public class PlaybackService : Service
{
    // ── Notification constants ─────────────────────────────────────────
    private const string ChannelId      = "orpheus_playback";
    private const string ChannelName    = "Orpheus Playback";
    private const int    NotificationId = 1;

    // ── Fields ────────────────────────────────────────────────────────
    private MobileViewModel?      _vm;
    private MediaSession?         _session;
    private MediaSessionCallback? _sessionCallback;
    private bool _isForeground;
    // Tracks whether the last metadata push had a zero duration so we know
    // to re-push once the real duration arrives from the player.
    private bool _metadataHasZeroDuration;

    // ──────────────────────────────────────────────────────────────────
    // Service lifecycle
    // ──────────────────────────────────────────────────────────────────

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();

        // Create the MediaSession unconditionally — it must exist before the
        // first StartForeground call. The ViewModel may not be ready yet
        // (Avalonia initializes asynchronously inside base.OnCreate of the
        // Activity), so VM attachment is deferred to TryAttachViewModel().
        _session = new MediaSession(this, "OrpheusMP");

#pragma warning disable CS0618 // SetFlags deprecated on API 33+ but still functional
        _session.SetFlags(
            MediaSessionFlags.HandlesMediaButtons |
            MediaSessionFlags.HandlesTransportControls);
#pragma warning restore CS0618

        _session.SetMediaButtonReceiver(
            PendingIntent.GetBroadcast(
                this, 0,
                new Intent(Intent.ActionMediaButton, null, this, typeof(MediaButtonReceiver)),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));

        _session.Active = true;

        TryAttachViewModel();
    }

    /// <summary>
    /// Resolves the ViewModel and wires up property-change listeners.
    /// Safe to call multiple times — no-ops if already attached.
    /// </summary>
    private void TryAttachViewModel()
    {
        if (_vm is not null) return;

        _vm = (global::Avalonia.Application.Current as App)?.ViewModel;
        if (_vm is null) return;

        _sessionCallback = new MediaSessionCallback(_vm);
        _session?.SetCallback(_sessionCallback);

        _vm.PropertyChanged += OnVmPropertyChanged;
        // Only sync immediately if the VM already has track data loaded.
        // If not, property change events will drive the first sync once
        // the async initialization completes.
        if (!string.IsNullOrEmpty(_vm.NowPlayingTitle))
        {
            UpdateSessionState();
            UpdateSessionMetadata();
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Avalonia may have finished initializing since OnCreate — retry attachment.
        TryAttachViewModel();

        var action = intent?.Action;

        // ── Raw MEDIA_BUTTON broadcast (headset / keyboard / BT) ─────
        if (action == Intent.ActionMediaButton && _vm is not null)
        {
#pragma warning disable CS0618 // GetParcelableExtra(string) deprecated on API 33+; replacement requires a Type overload not available on net10 bindings
            var keyEvent = (KeyEvent?)intent?.GetParcelableExtra(Intent.ExtraKeyEvent);
#pragma warning restore CS0618
            if (keyEvent?.Action == KeyEventActions.Down)
            {
                switch (keyEvent.KeyCode)
                {
                    case Keycode.MediaPlay:
                    case Keycode.MediaPlayPause:
                    case Keycode.MediaPause:
                    case Keycode.Headsethook:      // single-click headset button
                        _ = _vm.TogglePlayPauseAsync();
                        break;
                    case Keycode.MediaNext:
                        _ = _vm.PlayNextAsync();
                        break;
                    case Keycode.MediaPrevious:
                        _ = _vm.PlayPreviousAsync();
                        break;
                    case Keycode.MediaStop:
                        _ = _vm.StopAsync();
                        break;
                }
            }
        }

        // Ensure we are in the foreground with an up-to-date notification.
        ShowNotification();

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_session is not null)
        {
            _session.Active = false;
            _sessionCallback?.Dispose();
            _session.Release();
            _session = null;
        }

        if (_isForeground)
            StopForeground(StopForegroundFlags.Remove);

        base.OnDestroy();
    }

    // ──────────────────────────────────────────────────────────────────
    // ViewModel → MediaSession sync
    // ──────────────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MobileViewModel.IsPlaying):
            case nameof(MobileViewModel.IsActive):
                // Reactivate the session if the user dismissed the media widget —
                // dismissing it sets Active=false, so we restore it on state change.
                if (_session is not null && !_session.Active)
                {
                    _session.Active = true;
                    UpdateSessionMetadata();
                }
                UpdateSessionState();
                break;
            case nameof(MobileViewModel.NowPlayingTitle):
            case nameof(MobileViewModel.NowPlayingArtist):
            case nameof(MobileViewModel.NowPlayingAlbum):
                UpdateSessionMetadata();
                break;
            case nameof(MobileViewModel.PlaybackDuration):
                // Duration lives in MediaMetadata (sets the progress bar range).
                // Only re-push metadata if the previous push had duration=0 — i.e.
                // the real duration just arrived from the player for this track.
                // After that first fix-up we don't need to keep re-pushing.
                if (_metadataHasZeroDuration && _vm?.PlaybackDuration > 0)
                    UpdateSessionMetadata();
                UpdateSessionStateOnly();
                break;
            case nameof(MobileViewModel.PlaybackPosition):
                // Only update the session's playback state position — Android
                // interpolates the progress bar from the reported position +
                // timestamp + speed, so no notification re-post is needed here.
                UpdateSessionStateOnly();
                break;
        }
    }

    private void UpdateSessionState()
    {
        UpdateSessionStateOnly();

        // Refresh the foreground notification so the system media widget
        // reflects the updated play/pause/stop state immediately.
        if (_isForeground)
            ShowNotification();
    }

    /// <summary>
    /// Pushes the current position/speed/state to the MediaSession without
    /// re-posting the notification. Android interpolates the progress bar
    /// automatically from the reported position + elapsed time + playback
    /// speed, so this is all that's needed for position tick updates.
    /// </summary>
    private void UpdateSessionStateOnly()
    {
        if (_vm is null || _session is null) return;

        var stateCode = _vm.IsPlaying
            ? PlaybackStateCode.Playing
            : (_vm.IsActive ? PlaybackStateCode.Paused : PlaybackStateCode.Stopped);

        // Use the raw long constant fields exposed by the .NET Android binding.
        long actions =
            AndroidPlaybackState.ActionPlay          |
            AndroidPlaybackState.ActionPause         |
            AndroidPlaybackState.ActionPlayPause     |
            AndroidPlaybackState.ActionSkipToNext    |
            AndroidPlaybackState.ActionSkipToPrevious|
            AndroidPlaybackState.ActionStop          |
            AndroidPlaybackState.ActionSeekTo;

        var psb = new AndroidPlaybackState.Builder()
            .SetActions(actions)
            .SetState(stateCode,
                (long)(_vm.PlaybackPosition * 1000),
                1.0f)
            .Build();

        _session!.SetPlaybackState(psb);
    }

    private void UpdateSessionMetadata()
    {
        if (_vm is null || _session is null) return;

        var durationMs = (long)(_vm.PlaybackDuration * 1000);
        _metadataHasZeroDuration = durationMs <= 0;

        var builder = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle,  _vm.NowPlayingTitle)
            .PutString(MediaMetadata.MetadataKeyArtist, _vm.NowPlayingArtist)
            .PutString(MediaMetadata.MetadataKeyAlbum,  _vm.NowPlayingAlbum)
            .PutLong(MediaMetadata.MetadataKeyDuration, durationMs);

        var art = TryLoadAlbumArt();
        if (art is not null)
            builder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, art);

        _session!.SetMetadata(builder.Build());

        // Refresh the foreground notification so the system media widget
        // picks up the new title/artist/album immediately.
        if (_isForeground)
            ShowNotification();
    }

    private Bitmap? TryLoadAlbumArt()
    {
        try
        {
            var filePath = _vm?.CurrentFilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            var meta = new TagLibMetadataReader().ReadFromFile(filePath);
            if (meta.AlbumArt is null || meta.AlbumArt.Length == 0)
                return null;

            return BitmapFactory.DecodeByteArray(meta.AlbumArt, 0, meta.AlbumArt.Length);
        }
        catch { return null; }
    }

    // ──────────────────────────────────────────────────────────────────
    // Foreground notification with transport controls
    // ──────────────────────────────────────────────────────────────────

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr?.GetNotificationChannel(ChannelId) is not null) return;

        // Min importance — silent and does not appear in the shade.
        // The foreground service only needs a notification to exist, not be visible.
        // The system media widget (quick settings) handles all user-facing controls.
        var channel = new NotificationChannel(
            ChannelId, ChannelName, NotificationImportance.Min)
        {
            Description          = "Orpheus background playback",
            LockscreenVisibility = NotificationVisibility.Secret,
        };
        channel.SetSound(null, null);
        mgr?.CreateNotificationChannel(channel);
    }

    private void ShowNotification()
    {
        if (_session is null) return;

        // Minimal silent notification — only exists to satisfy the foreground
        // service requirement. The MediaStyle session token keeps the system
        // media widget linked to our session.
        var notification = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetVisibility(NotificationVisibility.Secret)
            .SetOngoing(true)
            .SetShowWhen(false)
            .SetStyle(new Notification.MediaStyle()
                .SetMediaSession(_session.SessionToken))
            .Build();

        if (!_isForeground)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, notification,
                    global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
            _isForeground = true;
        }
        else
        {
            var mgr = (NotificationManager?)GetSystemService(NotificationService);
            mgr?.Notify(NotificationId, notification);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // MediaSession.Callback — lock screen / Bluetooth / remote controls
    // ──────────────────────────────────────────────────────────────────

    private sealed class MediaSessionCallback : MediaSession.Callback
    {
        private readonly MobileViewModel _vm;
        public MediaSessionCallback(MobileViewModel vm) => _vm = vm;

        public override void OnPlay()           => _ = _vm.TogglePlayPauseAsync();
        public override void OnPause()          => _ = _vm.TogglePlayPauseAsync();
        public override void OnSkipToNext()     => _ = _vm.PlayNextAsync();
        public override void OnSkipToPrevious() => _ = _vm.PlayPreviousAsync();
        public override void OnStop()           => _ = _vm.StopAsync();
        public override void OnSeekTo(long pos) => _ = _vm.SeekToPositionAsync(pos / 1000.0);
    }
}

/// <summary>
/// Receives MEDIA_BUTTON broadcasts from hardware keys (headset buttons,
/// Bluetooth controllers, keyboards) and forwards them to
/// <see cref="PlaybackService"/> which decodes the <see cref="KeyEvent"/>.
/// </summary>
[BroadcastReceiver(
    Name = "net.orpheusmp.android.MediaButtonReceiver",
    Exported = true)]
[IntentFilter(new[] { Intent.ActionMediaButton },
    Priority = int.MaxValue)]
public class MediaButtonReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionMediaButton) return;

        // Only act on KEY_DOWN so we don't fire twice per button press.
#pragma warning disable CS0618
        var keyEvent = (KeyEvent?)intent.GetParcelableExtra(Intent.ExtraKeyEvent);
#pragma warning restore CS0618
        if (keyEvent?.Action != KeyEventActions.Down) return;

        var svcIntent = new Intent(context, typeof(PlaybackService));
        svcIntent.SetAction(Intent.ActionMediaButton);
        svcIntent.PutExtras(intent);
        context.StartForegroundService(svcIntent);
    }
}

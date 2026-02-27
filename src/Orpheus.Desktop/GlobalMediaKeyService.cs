using System;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Data;

namespace Orpheus.Desktop;

/// <summary>
/// The media actions the hotkey service can raise.
/// </summary>
public enum MediaAction
{
    PlayPause,
    NextTrack,
    PreviousTrack,
    Stop,
    VolumeUp,
    VolumeDown,
}

/// <summary>
/// Scroll wheel directions used in combo tokens.
/// </summary>
internal enum WheelDirection { None, Up, Down, Left, Right }

/// <summary>
/// A keyboard combination: zero or more modifiers plus a single key code.
/// </summary>
internal readonly record struct KeyCombo(EventMask Modifiers, KeyCode Key)
{
    public bool Matches(EventMask currentMask, KeyCode currentKey)
    {
        if (Key == KeyCode.VcUndefined) return false;
        if (currentKey != Key) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// A mouse-button combination: zero or more keyboard modifiers plus a mouse button.
/// </summary>
internal readonly record struct MouseButtonCombo(EventMask Modifiers, MouseButton Button)
{
    public bool Matches(EventMask currentMask, MouseButton currentButton)
    {
        if (Button == MouseButton.NoButton) return false;
        if (currentButton != Button) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// A scroll-wheel combination: zero or more keyboard modifiers plus a wheel direction.
/// </summary>
internal readonly record struct WheelCombo(EventMask Modifiers, WheelDirection Direction)
{
    public bool Matches(EventMask currentMask, WheelDirection currentDir)
    {
        if (Direction == WheelDirection.None) return false;
        if (currentDir != Direction) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// Captures global keyboard, mouse-button, and scroll-wheel events using SharpHook
/// and raises <see cref="ActionPressed"/> for configured shortcut combinations.
/// Bindings are stored as human-readable combo strings (e.g. "Ctrl+WheelUp", "Mouse4", "MediaPlay").
/// </summary>
public sealed class GlobalMediaKeyService : IDisposable
{
    private readonly EventLoopGlobalHook _hook;
    private bool _disposed;

    // 3 combo slots per action × 6 actions = 18 fields
    private KeyCombo         _playPauseKey,  _nextTrackKey,  _previousTrackKey,  _stopKey,  _volumeUpKey,  _volumeDownKey;
    private MouseButtonCombo _playPauseMouse, _nextTrackMouse, _previousTrackMouse, _stopMouse, _volumeUpMouse, _volumeDownMouse;
    private WheelCombo       _playPauseWheel, _nextTrackWheel, _previousTrackWheel, _stopWheel, _volumeUpWheel, _volumeDownWheel;

    /// <summary>
    /// When true, all hotkey matching is suppressed. Set while the user is
    /// actively rebinding a shortcut so that existing bindings do not fire.
    /// </summary>
    public volatile bool IsListening;

    /// <summary>
    /// Raised on the SharpHook event-loop thread when a configured hotkey fires.
    /// </summary>
    public event Action<MediaAction>? ActionPressed;

    public GlobalMediaKeyService()
    {
        _hook = new EventLoopGlobalHook(runAsyncOnBackgroundThread: true);
        _hook.KeyPressed   += OnKeyPressed;
        _hook.MousePressed += OnMousePressed;
        _hook.MouseWheel   += OnMouseWheel;
    }

    /// <summary>
    /// Applies shortcut configuration from combo strings. Can be called at any time
    /// to update bindings at runtime (e.g. after user changes settings).
    /// </summary>
    public void Configure(
        string playPause, string nextTrack, string previousTrack,
        string stop, string volumeUp, string volumeDown)
    {
        ParseBinding(playPause,      KeyCode.VcMediaPlay,     out _playPauseKey,     out _playPauseMouse,     out _playPauseWheel);
        ParseBinding(nextTrack,      KeyCode.VcMediaNext,     out _nextTrackKey,     out _nextTrackMouse,     out _nextTrackWheel);
        ParseBinding(previousTrack,  KeyCode.VcMediaPrevious, out _previousTrackKey, out _previousTrackMouse, out _previousTrackWheel);
        ParseBinding(stop,           KeyCode.VcMediaStop,     out _stopKey,          out _stopMouse,          out _stopWheel);
        ParseBinding(volumeUp,       KeyCode.VcVolumeUp,      out _volumeUpKey,      out _volumeUpMouse,      out _volumeUpWheel);
        ParseBinding(volumeDown,     KeyCode.VcVolumeDown,    out _volumeDownKey,    out _volumeDownMouse,    out _volumeDownWheel);
    }

    /// <summary>
    /// Start the global input hook on a background thread.
    /// </summary>
    public Task StartAsync() => _hook.RunAsync();

    // ── Event handlers ───────────────────────────────────────

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var key  = e.Data.KeyCode;

        if (IsModifierKey(key)) return;

        if      (_playPauseKey.Matches(mask, key))     ActionPressed?.Invoke(MediaAction.PlayPause);
        else if (_nextTrackKey.Matches(mask, key))      ActionPressed?.Invoke(MediaAction.NextTrack);
        else if (_previousTrackKey.Matches(mask, key))  ActionPressed?.Invoke(MediaAction.PreviousTrack);
        else if (_stopKey.Matches(mask, key))           ActionPressed?.Invoke(MediaAction.Stop);
        else if (_volumeUpKey.Matches(mask, key))       ActionPressed?.Invoke(MediaAction.VolumeUp);
        else if (_volumeDownKey.Matches(mask, key))     ActionPressed?.Invoke(MediaAction.VolumeDown);
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var btn  = e.Data.Button;

        if      (_playPauseMouse.Matches(mask, btn))     ActionPressed?.Invoke(MediaAction.PlayPause);
        else if (_nextTrackMouse.Matches(mask, btn))      ActionPressed?.Invoke(MediaAction.NextTrack);
        else if (_previousTrackMouse.Matches(mask, btn))  ActionPressed?.Invoke(MediaAction.PreviousTrack);
        else if (_stopMouse.Matches(mask, btn))           ActionPressed?.Invoke(MediaAction.Stop);
        else if (_volumeUpMouse.Matches(mask, btn))       ActionPressed?.Invoke(MediaAction.VolumeUp);
        else if (_volumeDownMouse.Matches(mask, btn))     ActionPressed?.Invoke(MediaAction.VolumeDown);
    }

    private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var dir  = GetWheelDirection(e.Data);

        if      (_playPauseWheel.Matches(mask, dir))     ActionPressed?.Invoke(MediaAction.PlayPause);
        else if (_nextTrackWheel.Matches(mask, dir))      ActionPressed?.Invoke(MediaAction.NextTrack);
        else if (_previousTrackWheel.Matches(mask, dir))  ActionPressed?.Invoke(MediaAction.PreviousTrack);
        else if (_stopWheel.Matches(mask, dir))           ActionPressed?.Invoke(MediaAction.Stop);
        else if (_volumeUpWheel.Matches(mask, dir))       ActionPressed?.Invoke(MediaAction.VolumeUp);
        else if (_volumeDownWheel.Matches(mask, dir))     ActionPressed?.Invoke(MediaAction.VolumeDown);
    }

    // ── Parsing ──────────────────────────────────────────────

    internal static WheelDirection GetWheelDirection(MouseWheelEventData data)
    {
        if (data.Rotation == 0) return WheelDirection.None;

        return data.Direction == MouseWheelScrollDirection.Horizontal
            ? (data.Rotation > 0 ? WheelDirection.Left  : WheelDirection.Right)
            : (data.Rotation > 0 ? WheelDirection.Up    : WheelDirection.Down);
    }

    /// <summary>
    /// Parses a combo string (e.g. "Ctrl+WheelUp", "Mouse4", "MediaPlay") into
    /// a KeyCombo, MouseButtonCombo, or WheelCombo. Only one of the three will
    /// be set; the other two remain default (unset).
    /// </summary>
    internal static void ParseBinding(string combo, KeyCode fallbackKey,
        out KeyCombo keyCombo, out MouseButtonCombo mouseCombo, out WheelCombo wheelCombo)
    {
        keyCombo   = default;
        mouseCombo = default;
        wheelCombo = default;

        if (string.IsNullOrWhiteSpace(combo))
        {
            keyCombo = new KeyCombo(EventMask.None, fallbackKey);
            return;
        }

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            keyCombo = new KeyCombo(EventMask.None, fallbackKey);
            return;
        }

        var modifiers = EventMask.None;
        var keyPart   = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control"        => EventMask.LeftCtrl,
                "alt"                      => EventMask.LeftAlt,
                "shift"                    => EventMask.LeftShift,
                "meta" or "super" or "win" => EventMask.LeftMeta,
                _                          => EventMask.None,
            };
        }

        // Wheel token?
        var wheelDir = ParseWheelDirection(keyPart);
        if (wheelDir != WheelDirection.None)
        {
            wheelCombo = new WheelCombo(modifiers, wheelDir);
            return;
        }

        // Mouse button token?
        var mouseButton = ParseMouseButton(keyPart);
        if (mouseButton != MouseButton.NoButton)
        {
            mouseCombo = new MouseButtonCombo(modifiers, mouseButton);
            return;
        }

        keyCombo = new KeyCombo(modifiers, ParseKeyCode(keyPart, fallbackKey));
    }

    internal static WheelDirection ParseWheelDirection(string token) =>
        token.ToLowerInvariant() switch
        {
            "wheelup"    => WheelDirection.Up,
            "wheeldown"  => WheelDirection.Down,
            "wheelleft"  => WheelDirection.Left,
            "wheelright" => WheelDirection.Right,
            _            => WheelDirection.None,
        };

    internal static MouseButton ParseMouseButton(string token) =>
        token.ToLowerInvariant() switch
        {
            "mouse1" => MouseButton.Button1,
            "mouse2" => MouseButton.Button2,
            "mouse3" => MouseButton.Button3,
            "mouse4" => MouseButton.Button4,
            "mouse5" => MouseButton.Button5,
            _        => MouseButton.NoButton,
        };

    private static KeyCode ParseKeyCode(string name, KeyCode fallback)
    {
        if (Enum.TryParse<KeyCode>(name, ignoreCase: true, out var code))
            return code;

        if (Enum.TryParse<KeyCode>("Vc" + name, ignoreCase: true, out code))
            return code;

        return fallback;
    }

    private static bool IsModifierKey(KeyCode key) => key is
        KeyCode.VcLeftShift    or KeyCode.VcRightShift   or
        KeyCode.VcLeftControl  or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt      or KeyCode.VcRightAlt     or
        KeyCode.VcLeftMeta     or KeyCode.VcRightMeta;

    /// <summary>
    /// Formats a combo string for display: "Ctrl+Mouse4" -> "Ctrl + Mouse 4".
    /// </summary>
    public static string FormatCombo(string combo)
    {
        if (string.IsNullOrEmpty(combo)) return "(None)";

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "(None)";

        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].ToLowerInvariant() switch
            {
                "mouse1"     => "Mouse Left",
                "mouse2"     => "Mouse Right",
                "mouse3"     => "Mouse Middle",
                "mouse4"     => "Mouse 4",
                "mouse5"     => "Mouse 5",
                "wheelup"    => "Wheel Up",
                "wheeldown"  => "Wheel Down",
                "wheelleft"  => "Wheel Left",
                "wheelright" => "Wheel Right",
                _ when parts[i].StartsWith("Vc", StringComparison.OrdinalIgnoreCase) => parts[i][2..],
                _ => parts[i],
            };
        }

        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.KeyPressed   -= OnKeyPressed;
        _hook.MousePressed -= OnMousePressed;
        _hook.MouseWheel   -= OnMouseWheel;
        _hook.Dispose();
    }
}

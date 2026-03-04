#if LINUX
// MprisService.cs — MPRIS2 D-Bus server for Linux.
// Compiled and active only on Linux (guarded by the LINUX preprocessor constant,
// defined in the .csproj when building on Linux).
//
// Exposes:
//   org.mpris.MediaPlayer2         — identity / raise / quit
//   org.mpris.MediaPlayer2.Player  — transport control & state
//
// Media keys (Next, Previous, Play, Pause, Stop) are forwarded to the callbacks
// wired by MainWindow so that desktop environments, playerctl, and KDE/GNOME
// media-key integrations work even when the app window is not focused.
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Orpheus.Desktop;

/// <summary>
/// Snapshot of the player state. Passed into <see cref="MprisService.UpdateState"/>
/// whenever something changes.
/// </summary>
public sealed class MprisPlayerState
{
    public bool   IsPlaying         { get; init; }
    public bool   IsActive          { get; init; }   // false = Stopped
    public bool   IsShuffleEnabled  { get; init; }
    /// <summary>"None" | "Track" | "Playlist" — the MPRIS LoopStatus string.</summary>
    public string RepeatMode        { get; init; } = "None";
    public double Volume            { get; init; }   // 0–100
    public double PositionSeconds   { get; init; }
    public double DurationSeconds   { get; init; }

    // Metadata
    public string? Title   { get; init; }
    public string? Artist  { get; init; }
    public string? Album   { get; init; }
    /// <summary>
    /// D-Bus object path for the current track, e.g.
    /// <c>/org/orpheusmp/track/42</c>. Leave null to use the NoTrack sentinel.
    /// </summary>
    public string? TrackId { get; init; }
}

/// <summary>
/// MPRIS2 D-Bus service. Registers <c>org.mpris.MediaPlayer2.OrpheusMP</c>
/// on the session bus and handles incoming transport commands.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class MprisService : IDisposable
{
    // ── D-Bus identifiers ────────────────────────────────────────────────────

    private const string BusName         = "org.mpris.MediaPlayer2.OrpheusMP";
    private const string MprisObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootIface       = "org.mpris.MediaPlayer2";
    private const string PlayerIface     = "org.mpris.MediaPlayer2.Player";
    private const string PropsIface      = "org.freedesktop.DBus.Properties";
    private const string NoTrack         = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    // ── State ────────────────────────────────────────────────────────────────

    private DBusConnection?  _connection;
    private MprisPlayerState _state     = new();
    private bool             _disposed;
    /// <summary>
    /// Timestamp of the last time we included Position in a PropertiesChanged
    /// signal. Used to throttle position updates to at most once per second so
    /// we give KDE's progress bar a regular heartbeat without flooding the bus.
    /// </summary>
    private long _lastPositionSignalTicks;

    // ── Callbacks wired by MainWindow ────────────────────────────────────────

    internal Func<Task>?     OnPlayPause;
    internal Func<Task>?     OnPlay;
    internal Func<Task>?     OnPause;
    internal Func<Task>?     OnStop;
    internal Func<Task>?     OnNext;
    internal Func<Task>?     OnPrevious;
    /// <summary>Seek to absolute position in seconds.</summary>
    internal Action<double>? OnSeekTo;
    /// <summary>Set volume 0–100.</summary>
    internal Action<double>? OnSetVolume;
    /// <summary>Set shuffle on/off.</summary>
    internal Action<bool>?   OnSetShuffle;
    /// <summary>Set loop/repeat mode. Receives the MPRIS string: "None", "Track", or "Playlist".</summary>
    internal Action<string>? OnSetLoopStatus;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Wire up playback callbacks before calling <see cref="StartAsync"/>.
    /// </summary>
    public void Configure(
        Func<Task>     playPause,
        Func<Task>     next,
        Func<Task>     previous,
        Func<Task>     stop,
        Func<Task>     play,
        Func<Task>     pause,
        Action<double> seekTo,
        Action<double> setVolume,
        Action<bool>   setShuffle,
        Action<string> setLoopStatus)
    {
        OnPlayPause    = playPause;
        OnNext         = next;
        OnPrevious     = previous;
        OnStop         = stop;
        OnPlay         = play;
        OnPause        = pause;
        OnSeekTo       = seekTo;
        OnSetVolume    = setVolume;
        OnSetShuffle   = setShuffle;
        OnSetLoopStatus = setLoopStatus;
    }

    /// <summary>
    /// Connect to the session bus, request the MPRIS bus name, and register
    /// the object handler. Safe to call even if the session bus is unavailable.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            _connection = new DBusConnection(DBusAddress.Session!);
            await _connection.ConnectAsync();
            await _connection.RequestNameAsync(BusName, RequestNameOptions.Default);
            _connection.AddMethodHandler(new MprisHandler(this));
        }
        catch (Exception ex)
        {
            // Non-fatal — D-Bus may not be available (headless CI, Wayland-only
            // without socket, etc.). Log and continue without MPRIS support.
            System.Diagnostics.Trace.WriteLine(
                $"[MprisService] Could not start: {ex.Message}");
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// Push a full player state snapshot. Emits <c>PropertiesChanged</c> for
    /// each property that has actually changed since the last call.
    /// </summary>
    public void UpdateState(MprisPlayerState newState)
    {
        if (_connection is null || _disposed)
            return;

        var old  = _state;
        _state   = newState;

        var changed = new Dictionary<string, VariantValue>();

        string newStatus = PlaybackStatus(newState);
        if (newStatus != PlaybackStatus(old))
            changed["PlaybackStatus"] = VariantValue.String(newStatus);

        if (newState.IsShuffleEnabled != old.IsShuffleEnabled)
            changed["Shuffle"] = VariantValue.Bool(newState.IsShuffleEnabled);

        if (newState.RepeatMode != old.RepeatMode)
            changed["LoopStatus"] = VariantValue.String(newState.RepeatMode);

        double newVol = Math.Clamp(newState.Volume / 100.0, 0.0, 1.0);
        double oldVol = Math.Clamp(old.Volume / 100.0, 0.0, 1.0);
        if (Math.Abs(newVol - oldVol) > 0.001)
            changed["Volume"] = VariantValue.Double(newVol);

        // Position: include at most once per second while playing so KDE's
        // progress bar advances. The MPRIS spec discourages high-frequency
        // Position notifications (clients should interpolate between ticks),
        // but KDE does use the value when present. We gate it to 1 Hz to
        // keep bus traffic low without starving the progress display.
        // Seeks are always communicated via the separate Seeked signal.
        if (newState.IsPlaying)
        {
            long now  = Environment.TickCount64;
            long elapsed = now - _lastPositionSignalTicks;
            if (elapsed >= 1_000) // 1 second in ms
            {
                changed["Position"] = VariantValue.Int64((long)(newState.PositionSeconds * 1_000_000));
                _lastPositionSignalTicks = now;
            }
        }

        bool metaChanged =
            newState.Title           != old.Title  ||
            newState.Artist          != old.Artist ||
            newState.Album           != old.Album  ||
            newState.TrackId         != old.TrackId||
            Math.Abs(newState.DurationSeconds - old.DurationSeconds) > 0.5;

        if (metaChanged)
            changed["Metadata"] = BuildMetadata(newState);

        if (changed.Count == 0)
            return;
        // Diagnostic: log what we are publishing to MPRIS
        try
        {
            var keys = string.Join(", ", changed.Keys);
            Console.WriteLine($"[MPRIS] properties changed: {keys}; newState playing={newState.IsPlaying}, active={newState.IsActive}, vol={newState.Volume}, pos={newState.PositionSeconds}");
        }
        catch { /* ignore logging failures in release */ }
        SendPropertiesChanged(PlayerIface, changed);
    }

    /// <summary>Emit the <c>Seeked</c> signal (position jumped).</summary>
    public void EmitSeeked(double positionSeconds)
    {
        if (_connection is null || _disposed) return;

        long us = (long)(positionSeconds * 1_000_000);
        Console.WriteLine($"[MPRIS] EmitSeeked signal queued: PositionSeconds={positionSeconds}, us={us}");
        using var w = _connection.GetMessageWriter();
        w.WriteSignalHeader(
            path:       MprisObjectPath,
            @interface: PlayerIface,
            member:     "Seeked",
            signature:  "x");
        w.WriteInt64(us);
        _connection.TrySendMessage(w.CreateMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
        _connection = null;
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private static string PlaybackStatus(MprisPlayerState s)
        => !s.IsActive ? "Stopped" : s.IsPlaying ? "Playing" : "Paused";

    /// <summary>
    /// Build the MPRIS Metadata value (type <c>a{sv}</c>) as a
    /// <see cref="VariantValue"/> of type Dictionary.
    /// </summary>
    internal static VariantValue BuildMetadata(MprisPlayerState s)
    {
        var d = new Dict<string, VariantValue>();

        d["mpris:trackid"] = VariantValue.ObjectPath(
            new ObjectPath(string.IsNullOrEmpty(s.TrackId) ? NoTrack : s.TrackId));

        // Always include mpris:length (even when zero) so KDE's media widget
        // always renders the seek scrubber. If omitted when duration is not yet
        // known, the widget hides the scrubber and won't show it again until
        // the trackid changes (i.e. the next track).
        d["mpris:length"] = VariantValue.Int64(
            (long)(s.DurationSeconds * 1_000_000));

        if (!string.IsNullOrEmpty(s.Title))
            d["xesam:title"] = VariantValue.String(s.Title!);

        if (!string.IsNullOrEmpty(s.Artist))
            d["xesam:artist"] = VariantValue.Array(new[] { s.Artist! });

        if (!string.IsNullOrEmpty(s.Album))
            d["xesam:album"] = VariantValue.String(s.Album!);

        return ((IVariantValueConvertable)d).AsVariantValue();
    }

    private void SendPropertiesChanged(
        string iface,
        Dictionary<string, VariantValue> changed)
    {
        if (_connection is null) return;

        // org.freedesktop.DBus.Properties.PropertiesChanged  body: sa{sv}as
        using var w = _connection.GetMessageWriter();
        w.WriteSignalHeader(
            path:       MprisObjectPath,
            @interface: PropsIface,
            member:     "PropertiesChanged",
            signature:  "sa{sv}as");

        w.WriteString(iface);

        var dictStart = w.WriteDictionaryStart();
        foreach (var kv in changed)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(kv.Key);
            w.WriteVariant(kv.Value);
        }
        w.WriteDictionaryEnd(dictStart);

        // Empty invalidated-properties array
        var arrStart = w.WriteArrayStart(DBusType.String);
        w.WriteArrayEnd(arrStart);

        _connection.TrySendMessage(w.CreateMessage());
    }

    // ── Property serialisers (used by GetAll / Get) ──────────────────────────

    internal void WriteRootProps(ref MessageWriter w)
    {
        var ds = w.WriteDictionaryStart();
        WriteKV(ref w, "CanQuit",             VariantValue.Bool(true));
        WriteKV(ref w, "CanRaise",            VariantValue.Bool(true));
        WriteKV(ref w, "HasTrackList",        VariantValue.Bool(false));
        WriteKV(ref w, "Identity",            VariantValue.String("OrpheusMP"));
        WriteKV(ref w, "DesktopEntry",        VariantValue.String("orpheusmp"));
        WriteKV(ref w, "SupportedUriSchemes", VariantValue.Array(Array.Empty<string>()));
        WriteKV(ref w, "SupportedMimeTypes",  VariantValue.Array(Array.Empty<string>()));
        w.WriteDictionaryEnd(ds);
    }

    internal void WritePlayerProps(ref MessageWriter w)
    {
        var s  = _state;
        var ds = w.WriteDictionaryStart();
        WriteKV(ref w, "PlaybackStatus", VariantValue.String(PlaybackStatus(s)));
        WriteKV(ref w, "LoopStatus",     VariantValue.String(s.RepeatMode));
        WriteKV(ref w, "Rate",           VariantValue.Double(1.0));
        WriteKV(ref w, "Shuffle",        VariantValue.Bool(s.IsShuffleEnabled));
        WriteKV(ref w, "Metadata",       BuildMetadata(s));
        WriteKV(ref w, "Volume",         VariantValue.Double(Math.Clamp(s.Volume / 100.0, 0.0, 1.0)));
        WriteKV(ref w, "Position",       VariantValue.Int64((long)(s.PositionSeconds * 1_000_000)));
        WriteKV(ref w, "MinimumRate",    VariantValue.Double(1.0));
        WriteKV(ref w, "MaximumRate",    VariantValue.Double(1.0));
        WriteKV(ref w, "CanGoNext",      VariantValue.Bool(true));
        WriteKV(ref w, "CanGoPrevious",  VariantValue.Bool(true));
        WriteKV(ref w, "CanPlay",        VariantValue.Bool(true));
        WriteKV(ref w, "CanPause",       VariantValue.Bool(true));
        WriteKV(ref w, "CanSeek",        VariantValue.Bool(s.IsActive && s.DurationSeconds > 0));
        WriteKV(ref w, "CanControl",     VariantValue.Bool(true));
        w.WriteDictionaryEnd(ds);
    }

    private static void WriteKV(ref MessageWriter w, string key, VariantValue val)
    {
        w.WriteDictionaryEntryStart();
        w.WriteString(key);
        w.WriteVariant(val);
    }

    internal VariantValue? GetRootProp(string prop) => prop switch
    {
        "CanQuit"             => VariantValue.Bool(true),
        "CanRaise"            => VariantValue.Bool(true),
        "HasTrackList"        => VariantValue.Bool(false),
        "Identity"            => VariantValue.String("OrpheusMP"),
        "DesktopEntry"        => VariantValue.String("orpheusmp"),
        "SupportedUriSchemes" => VariantValue.Array(Array.Empty<string>()),
        "SupportedMimeTypes"  => VariantValue.Array(Array.Empty<string>()),
        _                     => (VariantValue?)null
    };

    internal VariantValue? GetPlayerProp(string prop)
    {
        var s = _state;
        return prop switch
        {
            "PlaybackStatus" => VariantValue.String(PlaybackStatus(s)),
            "LoopStatus"     => VariantValue.String(s.RepeatMode),
            "Rate"           => VariantValue.Double(1.0),
            "Shuffle"        => VariantValue.Bool(s.IsShuffleEnabled),
            "Metadata"       => BuildMetadata(s),
            "Volume"         => VariantValue.Double(Math.Clamp(s.Volume / 100.0, 0.0, 1.0)),
            "Position"       => VariantValue.Int64((long)(s.PositionSeconds * 1_000_000)),
            "MinimumRate"    => VariantValue.Double(1.0),
            "MaximumRate"    => VariantValue.Double(1.0),
            "CanGoNext"      => VariantValue.Bool(true),
            "CanGoPrevious"  => VariantValue.Bool(true),
            "CanPlay"        => VariantValue.Bool(true),
            "CanPause"       => VariantValue.Bool(true),
            "CanSeek"        => VariantValue.Bool(s.IsActive && s.DurationSeconds > 0),
            "CanControl"     => VariantValue.Bool(true),
            _                => (VariantValue?)null
        };
    }

    // ── Inner handler ────────────────────────────────────────────────────────

    private sealed class MprisHandler : IPathMethodHandler
    {
        private readonly MprisService _svc;

        public string Path           => MprisObjectPath;
        public bool   HandlesChildPaths => false;

        public MprisHandler(MprisService svc) => _svc = svc;

        public ValueTask HandleMethodAsync(MethodContext ctx)
        {
            if (ctx.IsDBusIntrospectRequest)
            {
                ctx.ReplyIntrospectXml([s_rootXml, s_playerXml]);
                return ValueTask.CompletedTask;
            }

            if (ctx.IsPropertiesInterfaceRequest)
            {
                HandleProps(ctx);
                return ValueTask.CompletedTask;
            }

            var iface  = ctx.Request.InterfaceAsString;
            var member = ctx.Request.MemberAsString;

            if (iface == RootIface || iface == "")
            {
                switch (member)
                {
                    case "Raise":
                    case "Quit":
                        ReplyVoid(ctx);
                        return ValueTask.CompletedTask;
                }
            }

            if (iface == PlayerIface || iface == "")
            {
                switch (member)
                {
                    case "PlayPause":
                        ReplyVoid(ctx);
                        _ = _svc.OnPlayPause?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Play":
                        ReplyVoid(ctx);
                        _ = _svc.OnPlay?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Pause":
                        ReplyVoid(ctx);
                        _ = _svc.OnPause?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Stop":
                        ReplyVoid(ctx);
                        _ = _svc.OnStop?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Next":
                        ReplyVoid(ctx);
                        _ = _svc.OnNext?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Previous":
                        ReplyVoid(ctx);
                        _ = _svc.OnPrevious?.Invoke();
                        return ValueTask.CompletedTask;

                    case "Seek":
                    {
                        var r       = ctx.Request.GetBodyReader();
                        long offUs  = r.ReadInt64();
                        ReplyVoid(ctx);
                        double newPos = Math.Max(0,
                            _svc._state.PositionSeconds + offUs / 1_000_000.0);
                        _svc.OnSeekTo?.Invoke(newPos);
                        // Also emit Seeked signal so clients can update UI immediately
                        // when a seek occurs.
                        Console.WriteLine($"[MPRIS] Seek to {newPos}s");
                        _svc.EmitSeeked(newPos);
                        Console.WriteLine($"[MPRIS] EmitSeeked emitted for {newPos}s");
                        return ValueTask.CompletedTask;
                    }

                    case "SetPosition":
                    {
                        var r      = ctx.Request.GetBodyReader();
                        _          = r.ReadObjectPath(); // trackId — ignored
                        long posUs = r.ReadInt64();
                        ReplyVoid(ctx);
                        _svc.OnSeekTo?.Invoke(posUs / 1_000_000.0);
                        return ValueTask.CompletedTask;
                    }

                    case "OpenUri":
                        ReplyVoid(ctx);
                        return ValueTask.CompletedTask;
                }
            }

            // Unknown — MethodContext will auto-reply with UnknownMethod on Dispose.
            return ValueTask.CompletedTask;
        }

        // ── org.freedesktop.DBus.Properties ─────────────────────────────────

        private void HandleProps(MethodContext ctx)
        {
            var member = ctx.Request.MemberAsString;
            switch (member)
            {
                case "GetAll":
                {
                    var r     = ctx.Request.GetBodyReader();
                    var iface = r.ReadString();
                    // MessageWriter is a ref struct; avoid `using var` so we
                    // can pass it as `ref` to the helper methods.
                    var w = ctx.CreateReplyWriter("a{sv}");
                    try
                    {
                        if (iface == RootIface)
                            _svc.WriteRootProps(ref w);
                        else if (iface == PlayerIface)
                            _svc.WritePlayerProps(ref w);
                        else
                        {
                            var ds = w.WriteDictionaryStart();
                            w.WriteDictionaryEnd(ds);
                        }
                        ctx.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    break;
                }

                case "Get":
                {
                    var r      = ctx.Request.GetBodyReader();
                    var iface  = r.ReadString();
                    var prop   = r.ReadString();

                    VariantValue? val = iface == RootIface
                        ? _svc.GetRootProp(prop)
                        : iface == PlayerIface
                            ? _svc.GetPlayerProp(prop)
                            : null;

                    if (val is null)
                    {
                        ctx.ReplyError("org.freedesktop.DBus.Error.UnknownProperty",
                            $"Unknown property '{prop}' on interface '{iface}'");
                        break;
                    }

                    using var w = ctx.CreateReplyWriter("v");
                    w.WriteVariant(val.Value);
                    ctx.Reply(w.CreateMessage());
                    break;
                }

                case "Set":
                {
                    var r    = ctx.Request.GetBodyReader();
                    var iface = r.ReadString();
                    var prop  = r.ReadString();

                    if (iface == PlayerIface)
                    {
                        switch (prop)
                        {
                            case "Volume":
                            {
                                var variant = r.ReadVariantValue();
                                double vol = variant.GetDouble() * 100.0;
                                ReplyVoid(ctx);
                                _svc.OnSetVolume?.Invoke(Math.Clamp(vol, 0, 100));
                                return;
                            }
                            case "Shuffle":
                            {
                                var variant = r.ReadVariantValue();
                                bool shuffle = variant.GetBool();
                                ReplyVoid(ctx);
                                _svc.OnSetShuffle?.Invoke(shuffle);
                                return;
                            }
                            case "LoopStatus":
                            {
                                var variant = r.ReadVariantValue();
                                string loop = variant.GetString();
                                ReplyVoid(ctx);
                                _svc.OnSetLoopStatus?.Invoke(loop);
                                return;
                            }
                            case "Rate":
                                ReplyVoid(ctx);
                                return;
                        }
                    }

                    ctx.ReplyError(
                        "org.freedesktop.DBus.Error.PropertyReadOnly",
                        $"Property '{prop}' on interface '{iface}' is read-only or unknown.");
                    break;
                }

                default:
                    // auto-replies UnknownMethod on Dispose
                    break;
            }
        }

        private static void ReplyVoid(MethodContext ctx)
        {
            using var w = ctx.DBusConnection.GetMessageWriter();
            w.WriteMethodReturnHeader(
                replySerial: ctx.Request.Serial,
                destination: ctx.Request.Sender);
            ctx.Reply(w.CreateMessage());
        }

        // ── Introspection XML ────────────────────────────────────────────────

        private static readonly ReadOnlyMemory<byte> s_rootXml = """
            <interface name="org.mpris.MediaPlayer2">
              <method name="Raise"/>
              <method name="Quit"/>
              <property name="CanQuit"             type="b"  access="read"/>
              <property name="CanRaise"            type="b"  access="read"/>
              <property name="HasTrackList"        type="b"  access="read"/>
              <property name="Identity"            type="s"  access="read"/>
              <property name="DesktopEntry"        type="s"  access="read"/>
              <property name="SupportedUriSchemes" type="as" access="read"/>
              <property name="SupportedMimeTypes"  type="as" access="read"/>
            </interface>
            """u8.ToArray();

        private static readonly ReadOnlyMemory<byte> s_playerXml = """
            <interface name="org.mpris.MediaPlayer2.Player">
              <method name="Next"/>
              <method name="Previous"/>
              <method name="Pause"/>
              <method name="PlayPause"/>
              <method name="Stop"/>
              <method name="Play"/>
              <method name="Seek">
                <arg direction="in"  type="x" name="Offset"/>
              </method>
              <method name="SetPosition">
                <arg direction="in"  type="o" name="TrackId"/>
                <arg direction="in"  type="x" name="Position"/>
              </method>
              <method name="OpenUri">
                <arg direction="in"  type="s" name="Uri"/>
              </method>
              <signal name="Seeked">
                <arg type="x" name="Position"/>
              </signal>
              <property name="PlaybackStatus" type="s"     access="read"/>
              <property name="LoopStatus"     type="s"     access="readwrite"/>
              <property name="Rate"           type="d"     access="readwrite"/>
              <property name="Shuffle"        type="b"     access="readwrite"/>
              <property name="Metadata"       type="a{sv}" access="read"/>
              <property name="Volume"         type="d"     access="readwrite"/>
              <property name="Position"       type="x"     access="read"/>
              <property name="MinimumRate"    type="d"     access="read"/>
              <property name="MaximumRate"    type="d"     access="read"/>
              <property name="CanGoNext"      type="b"     access="read"/>
              <property name="CanGoPrevious"  type="b"     access="read"/>
              <property name="CanPlay"        type="b"     access="read"/>
              <property name="CanPause"       type="b"     access="read"/>
              <property name="CanSeek"        type="b"     access="read"/>
              <property name="CanControl"     type="b"     access="read"/>
            </interface>
            """u8.ToArray();
    }
}

#endif

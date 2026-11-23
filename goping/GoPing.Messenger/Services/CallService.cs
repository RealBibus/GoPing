using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using GoPing.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GoPing.Services;

public enum CallStatus
{
    Idle,
    Incoming,
    Outgoing,
    Active,
    Ended
}

public sealed record CallInfo(string CallId, string ChatId, string PeerUserId, string PeerName);

public enum ScreenShareKind { Monitor, Window }

public sealed record ScreenShareSource(ScreenShareKind Kind, string Name, nint Handle, int Left, int Top, int Width, int Height);

public sealed record CallStatsSnapshot(
    int? RttMs,
    double? JitterMs,
    int? PacketsLost,
    int? PacketsReceived,
    string? Codec,
    IReadOnlyList<CallJitterPoint> History);

public sealed record CallJitterPoint(long T, double Jitter, int? Rtt);

/// <summary>
/// 1:1 calls via WebView2 WebRTC engine (Assets/call). Signaling goes over existing WebSocket.
/// Custom WPF mic/screen permission UI stays in MainWindow.
/// </summary>
public sealed class CallService
{
    private readonly ApiClient _api;
    private readonly Func<string, Task> _sendAsync;
    private readonly Action<string> _dropPending;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();

    private WebView2? _webView;
    private Task? _initTask;
    private bool _coreReady;
    private List<object>? _iceServers;
    private string _iceServersJson = "{\"iceServers\":[{\"urls\":\"stun:stun.l.google.com:19302\"}]}";

    private CallStatus _state = CallStatus.Idle;
    private CallInfo? _current;
    private bool _muted;
    private bool _deafened;
    private bool _localSignalsReady;
    private readonly Queue<(string kind, JsonElement data)> _pendingRemoteSignals = new();
    private ScreenShareSource? _pendingScreenSource;
    private CancellationTokenSource? _screenCaptureCts;
    private Task? _screenCaptureTask;
    private WgcScreenCapturer? _wgcCapturer;

    public CallService(ApiClient api, Func<string, Task> sendAsync, Action<string>? dropPending = null)
    {
        _api = api;
        _sendAsync = sendAsync;
        _dropPending = dropPending ?? (_ => { });
    }

    public CallInfo? Current => _current;
    public bool IsMuted => _muted;
    public bool IsDeafened => _deafened;
    public bool IsScreenSharing { get; private set; }
    public int ScreenShareFps { get; set; } = 30;
    public string ScreenCaptureMode { get; set; } = "Auto";
    public string ScreenShareQuality { get; set; } = "HD";

    public static (int MaxWidth, long JpegQuality) ResolveScreenShareQuality(string? quality)
    {
        return (quality ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "standard" or "sd" or "960p" => (960, 45L),
            "high" => (1280, 65L),
            "hd" or "1600p" => (1600, 75L),
            "fullhd" or "1080p" or "fhd" => (1920, 80L),
            "native" or "source" or "original" => (3840, 82L),
            _ => (1600, 75L)
        };
    }

    public event Action<CallInfo>? Incoming;
    public event Action<CallStatus, string>? StateChanged;
    public event Action<bool, bool>? SpeakingChanged;
    public event Action<byte[], int, int>? RemoteVideoFrameReady; // not used in WebView2 path
    public event Action? RemoteVideoCleared;
    public event Action<CallStatsSnapshot>? StatsUpdated;
    public event Action<bool, string?>? ScreenShareStateChanged;
    public event Action<bool, bool>? PeerMediaStateChanged;

    public Task InitializeAsync(WebView2? webView = null)
    {
        if (webView is not null) _webView = webView;
        return _initTask ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            RtcConfigResponse config = await _api.GetRtcConfigAsync();
            if (config.IceServers is { Count: > 0 })
            {
                _iceServers = config.IceServers.Cast<object>().ToList();
                _iceServersJson = JsonSerializer.Serialize(new { iceServers = config.IceServers }, _jsonOptions);
            }
            else
            {
                _iceServersJson = DefaultIceJson();
            }
            CallLogger.Info($"ICE config loaded for WebView2: {_iceServersJson}");
        }
        catch (Exception ex)
        {
            _iceServersJson = DefaultIceJson();
            CallLogger.Error("ICE config fetch failed, using STUN fallback", ex);
        }

        if (_webView is null)
        {
            CallLogger.Info("CallService WebView2 not attached; call engine deferred.");
            return;
        }

        await _webView.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await _webView.EnsureCoreWebView2Async();
                var core = _webView.CoreWebView2;
                _coreReady = core is not null;
                if (core is null) return;

                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;

                core.PermissionRequested += Core_PermissionRequested;
                try { core.ScreenCaptureStarting += Core_ScreenCaptureStarting; } catch (Exception ex) { CallLogger.Info($"ScreenCaptureStarting hook failed: {ex.Message}"); }

                var bridge = new CallBridge(this);
                core.AddHostObjectToScript("callBridge", bridge);

                string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "call");
                string callHtml = Path.Combine(assetsDir, "call.html");
                if (File.Exists(callHtml))
                {
                    core.Navigate(new Uri(callHtml).AbsoluteUri);
                }
                else
                {
                    CallLogger.Error($"call.html not found at {callHtml}");
                }
            }
            catch (Exception ex)
            {
                _coreReady = false;
                CallLogger.Error("WebView2 call engine init failed", ex);
            }
        });
    }

    private static string DefaultIceJson() =>
        "{\"iceServers\":[{\"urls\":[\"stun:stun.l.google.com:19302\",\"stun:stun1.l.google.com:19302\"]}]}";

    private void Core_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // WPF UI already gated mic/screen; auto-allow in engine to avoid double prompt.
        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone ||
            e.PermissionKind == CoreWebView2PermissionKind.Camera ||
            e.PermissionKind.ToString().Contains("Display", StringComparison.OrdinalIgnoreCase))
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private void Core_ScreenCaptureStarting(object? sender, CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        try
        {
            // This WebView2 SDK version doesn't expose display/window source selection.
            // Allow capture and let WebView2/default runtime UI handle source choice.
            e.Cancel = false;
            e.Handled = false;
            CallLogger.Info("ScreenCaptureStarting: allowing default capture UI");
        }
        catch (Exception ex)
        {
            CallLogger.Error("ScreenCaptureStarting handler failed", ex);
        }
    }

    private async Task SendAsync(object payload)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        string type = "unknown";
        try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString() ?? type; } catch { }
        CallLogger.Info($"send {type} callId={_current?.CallId}");
        await _sendAsync(json);
    }

    private void Emit(CallStatus status, string text)
    {
        _state = status;
        CallLogger.Info($"state={status} text='{text}' callId={_current?.CallId}");
        void Raise() => StateChanged?.Invoke(status, text);
        if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
        else Application.Current?.Dispatcher?.BeginInvoke(Raise);
    }

    private Task ExecJsAsync(string script)
    {
        var wv = _webView;
        if (wv is null) return Task.CompletedTask;
        if (wv.Dispatcher.CheckAccess())
            return wv.CoreWebView2 is null ? Task.CompletedTask : wv.CoreWebView2.ExecuteScriptAsync(script);
        return wv.Dispatcher.InvokeAsync(() =>
        {
            var core = wv.CoreWebView2;
            return core is null ? Task.CompletedTask : core.ExecuteScriptAsync(script);
        }).Task.Unwrap();
    }

    private async Task<string?> ExecJsResultAsync(string script)
    {
        var wv = _webView;
        if (wv is null) return null;
        if (wv.Dispatcher.CheckAccess())
        {
            var core = wv.CoreWebView2;
            return core is null ? null : await core.ExecuteScriptAsync(script);
        }
        return await wv.Dispatcher.InvokeAsync(async () =>
        {
            var core = wv.CoreWebView2;
            return core is null ? null : await core.ExecuteScriptAsync(script);
        }).Task.Unwrap();
    }

    private Task PostWebMessageAsync(object payload)
    {
        var wv = _webView;
        if (wv is null) return Task.CompletedTask;
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        if (wv.Dispatcher.CheckAccess())
        {
            if (wv.CoreWebView2 is not null) wv.CoreWebView2.PostWebMessageAsJson(json);
            return Task.CompletedTask;
        }
        return wv.Dispatcher.InvokeAsync(() =>
        {
            if (wv.CoreWebView2 is not null) wv.CoreWebView2.PostWebMessageAsJson(json);
        }).Task;
    }

    private static string JsStr(string s) => JsonSerializer.Serialize(s);
    private static string NormalizeCallId(string? s) => (s ?? string.Empty).Replace("-", string.Empty);
    private static bool SameCall(string? a, string? b) => string.Equals(NormalizeCallId(a), NormalizeCallId(b), StringComparison.OrdinalIgnoreCase);

    private async Task EnsureEngineReadyAsync()
    {
        await InitializeAsync();
        if (_webView is null) throw new InvalidOperationException("Call engine not attached.");
        // Wait briefly for navigation/core
        for (int i = 0; i < 40 && _webView.CoreWebView2 is null; i++)
            await Task.Delay(50);
        if (_webView.CoreWebView2 is null) throw new InvalidOperationException("WebView2 call engine unavailable.");
    }

    public async Task StartOutgoingAsync(string chatId, string peerUserId, string peerName)
    {
        await EnsureEngineReadyAsync();
        var call = new CallInfo(Guid.NewGuid().ToString("N"), chatId, peerUserId, peerName);
        lock (_lock)
        {
            _current = call;
            _muted = false;
            _deafened = false;
            _localSignalsReady = true;
            _pendingRemoteSignals.Clear();
        }
        IsScreenSharing = false;
        Emit(CallStatus.Outgoing, "Calling…");
        await ExecJsAsync($"window.gopingCall && window.gopingCall.prepare(true,{JsStr(_iceServersJson)})");
        await ExecJsAsync("window.gopingCall && window.gopingCall.startOutgoing()");
        await SendAsync(new { type = "call.invite", chatId, callId = call.CallId });
    }

    public async Task AcceptIncomingAsync()
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return;
        await EnsureEngineReadyAsync();
        lock (_lock) { _localSignalsReady = true; }
        Emit(CallStatus.Active, "Connecting…");
        await ExecJsAsync($"window.gopingCall && window.gopingCall.prepare(false,{JsStr(_iceServersJson)})");
        await ExecJsAsync("window.gopingCall && window.gopingCall.startIncoming()");
        await SendAsync(new { type = "call.accept", callId = call.CallId, chatId = call.ChatId });
        await DrainPendingSignalsAsync();
        await SendMediaStateAsync();
    }

    public async Task RejectIncomingAsync()
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return;
        await SendAsync(new { type = "call.reject", callId = call.CallId, chatId = call.ChatId });
        Finish("Declined", call.CallId);
    }

    public async Task EndAsync(string reason = "hangup")
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return;
        await SendAsync(new { type = "call.end", callId = call.CallId, chatId = call.ChatId, reason });
        Finish("Call ended", call.CallId);
    }

    public Task HandleInviteAsync(string callId, string chatId, string fromUserId, string peerName)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                // Busy - reject incoming
                _ = SendAsync(new { type = "call.reject", callId, chatId });
                return Task.CompletedTask;
            }
            _current = new CallInfo(callId, chatId, fromUserId, peerName);
            _localSignalsReady = false;
            _pendingRemoteSignals.Clear();
            _muted = false;
            _deafened = false;
            IsScreenSharing = false;
        }
        Emit(CallStatus.Incoming, "Incoming call");
        void Raise() => Incoming?.Invoke(_current!);
        if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
        else Application.Current?.Dispatcher?.BeginInvoke(Raise);
        return Task.CompletedTask;
    }

    public async Task HandleAcceptedAsync(string callId)
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null || !SameCall(call.CallId, callId)) return;
        Emit(CallStatus.Active, "Connecting…");
        await DrainPendingSignalsAsync();
        await SendMediaStateAsync();
    }

    public Task HandleRejectedAsync(string callId)
    {
        Finish("Call declined", callId);
        return Task.CompletedTask;
    }

    public Task HandleEndedAsync(string callId)
    {
        Finish("Call ended", callId);
        return Task.CompletedTask;
    }

    public async Task HandleSignalAsync(string callId, string kind, JsonElement data)
    {
        CallInfo? call;
        bool ready;
        lock (_lock) { call = _current; ready = _localSignalsReady; }
        if (call is null || !SameCall(call.CallId, callId)) return;

        if (string.Equals(kind, "media-state", StringComparison.OrdinalIgnoreCase))
        {
            bool peerMuted = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("muted", out var m) && m.GetBoolean();
            bool peerDeafened = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("deafened", out var d) && d.GetBoolean();
            void Raise() => PeerMediaStateChanged?.Invoke(peerMuted, peerDeafened);
            if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
            else Application.Current?.Dispatcher?.BeginInvoke(Raise);
            return;
        }

        if (!ready)
        {
            lock (_lock) _pendingRemoteSignals.Enqueue((kind, data.Clone()));
            CallLogger.Info($"buffered remote signal kind={kind} callId={callId}");
            return;
        }
        await ForwardSignalToEngineAsync(kind, data);
    }

    private async Task DrainPendingSignalsAsync()
    {
        List<(string kind, JsonElement data)> items;
        lock (_lock)
        {
            items = _pendingRemoteSignals.ToList();
            _pendingRemoteSignals.Clear();
        }
        foreach (var (kind, data) in items)
            await ForwardSignalToEngineAsync(kind, data);
    }

    private async Task ForwardSignalToEngineAsync(string kind, JsonElement data)
    {
        try
        {
            var msg = JsonSerializer.Serialize(new { kind, data }, _jsonOptions);
            CallLogger.Info($"forward signal kind={kind} callId={_current?.CallId}");
            await ExecJsAsync($"window.gopingCall && window.gopingCall.handleSignal({JsStr(msg)})");
        }
        catch (Exception ex)
        {
            CallLogger.Error($"forward signal failed kind={kind}", ex);
        }
    }

    public Task HandlePeerRejoinedAsync()
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return Task.CompletedTask;
        if (_state == CallStatus.Active)
            return ExecJsAsync("window.gopingCall && window.gopingCall.reconnect()");
        return Task.CompletedTask;
    }

    public Task OnRealtimeReconnectedAsync()
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return Task.CompletedTask;
        return SendAsync(new { type = "call.rejoin", callId = call.CallId, chatId = call.ChatId });
    }

    public Task SetMutedAsync(bool muted)
    {
        _muted = muted;
        _ = SendMediaStateAsync();
        return ExecJsAsync($"window.gopingCall && window.gopingCall.setMuted({(muted ? "true" : "false")})");
    }

    public Task SetDeafenedAsync(bool deafened)
    {
        _deafened = deafened;
        _ = SendMediaStateAsync();
        return ExecJsAsync($"window.gopingCall && window.gopingCall.setDeafened({(deafened ? "true" : "false")})");
    }

    public Task SetRemoteVolumeAsync(double volume)
    {
        double v = Math.Clamp(volume, 0.0, 4.0);
        return ExecJsAsync($"window.gopingCall && window.gopingCall.setRemoteVolume({v.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private Task SendMediaStateAsync()
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return Task.CompletedTask;
        return SendAsync(new
        {
            type = "call.signal",
            callId = call.CallId,
            chatId = call.ChatId,
            kind = "media-state",
            data = new { muted = _muted, deafened = _deafened }
        });
    }

    public async Task StartScreenShareAsync(ScreenShareSource source)
    {
        CallInfo? current;
        lock (_lock) current = _current;
        if (current is null) throw new InvalidOperationException("No active call.");

        _pendingScreenSource = source;
        await EnsureEngineReadyAsync();

        bool hasPc = false;
        for (int i = 0; i < 30; i++)
        {
            var result = await ExecJsResultAsync("window.gopingCall && window.gopingCall.hasActivePc ? window.gopingCall.hasActivePc() : false");
            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase)) { hasPc = true; break; }
            await Task.Delay(100);
        }
        if (!hasPc) throw new InvalidOperationException("Call is not connected yet. Try again in a moment.");

        await StopScreenCaptureLoopAsync(stopJs: false);

        _wgcCapturer?.Dispose();
        _wgcCapturer = null;
        var mode = (ScreenCaptureMode ?? "Auto").Trim();
        bool wantWgc = !mode.Equals("Gdi", StringComparison.OrdinalIgnoreCase);
        bool wantGdiFallback = !mode.Equals("Wgc", StringComparison.OrdinalIgnoreCase);
        if (wantWgc)
        {
            _wgcCapturer = WgcScreenCapturer.TryCreate(source);
            if (_wgcCapturer is null)
            {
                if (wantGdiFallback)
                    CallLogger.Info("WGC unavailable, falling back to GDI capture");
                else
                    throw new InvalidOperationException("WGC capture is unavailable on this PC. Switch capture mode to Auto or GDI.");
            }
        }
        else
        {
            CallLogger.Info("Screen capture mode: GDI");
        }

        int w = Math.Max(1, source.Width);
        int h = Math.Max(1, source.Height);
        int fps = Math.Clamp(ScreenShareFps, 30, 60);
        await ExecJsAsync($"window.gopingCall && window.gopingCall.startScreenShareExternal({w},{h},{fps})");

        _screenCaptureCts = new CancellationTokenSource();
        var ct = _screenCaptureCts.Token;
        var src = source;
        _screenCaptureTask = Task.Run(() => ScreenCaptureLoopAsync(src, ct), ct);
        IsScreenSharing = true;
    }

    public async Task StopScreenShareAsync()
    {
        await StopScreenCaptureLoopAsync(stopJs: true);
        IsScreenSharing = false;
        _pendingScreenSource = null;
    }

    private async Task StopScreenCaptureLoopAsync(bool stopJs)
    {
        try { _screenCaptureCts?.Cancel(); } catch { }
        if (_screenCaptureTask is not null)
        {
            try { await _screenCaptureTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _screenCaptureCts?.Dispose();
        _screenCaptureCts = null;
        _screenCaptureTask = null;
        _wgcCapturer?.Dispose();
        _wgcCapturer = null;
        if (stopJs)
            await ExecJsAsync("window.gopingCall && window.gopingCall.stopScreenShare().catch(e=>console.error(e))");
    }

    private async Task ScreenCaptureLoopAsync(ScreenShareSource src, CancellationToken ct)
    {
        int sent = 0;
        var sw = new System.Diagnostics.Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                var (maxW, jpegQ) = ResolveScreenShareQuality(ScreenShareQuality);
                var capturer = _wgcCapturer;
                string? dataUrl = capturer?.TryCaptureJpegBase64(maxW, jpegQ);
                if (dataUrl is null) dataUrl = await CaptureFrameDataUrlGdiAsync(src, maxW, jpegQ);
                if (dataUrl is not null)
                {
                    await PostWebMessageAsync(new { type = "screenFrame", dataUrl });
                    sent++;
                    if (sent == 1 || sent % 50 == 0)
                        CallLogger.Info($"screen frame sent #{sent} len={dataUrl.Length} mode={(capturer != null ? "wgc" : "gdi")} q={jpegQ} maxW={maxW}");
                }
                else if (sent == 0)
                {
                    CallLogger.Info("screen capture returned no frame yet");
                }
            }
            catch (Exception ex)
            {
                CallLogger.Error("screen capture frame failed", ex);
            }

            int fps = Math.Clamp(ScreenShareFps, 30, 60);
            int targetMs = 1000 / fps;
            int remaining = targetMs - (int)sw.ElapsedMilliseconds;
            if (remaining < 1) remaining = 1;
            try { await Task.Delay(remaining, ct); } catch { break; }
        }
    }

    private static Task<string?> CaptureFrameDataUrlGdiAsync(ScreenShareSource src, int maxW, long quality)
    {
        try
        {
            int left = src.Left, top = src.Top, w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
            if (src.Kind == ScreenShareKind.Window && src.Handle != IntPtr.Zero && GetWindowRect(src.Handle, out var wr))
            {
                w = Math.Max(1, wr.Right - wr.Left);
                h = Math.Max(1, wr.Bottom - wr.Top);
                left = wr.Left;
                top = wr.Top;
            }

            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h),
                    System.Drawing.CopyPixelOperation.SourceCopy);
                DrawCursorGdi(g, left, top);
            }

            int targetMaxW = Math.Max(320, maxW);
            System.Drawing.Bitmap toEncode = bmp;
            bool scaled = false;
            if (w > targetMaxW)
            {
                int nh = Math.Max(1, (int)(h * (targetMaxW / (double)w)));
                var small = new System.Drawing.Bitmap(targetMaxW, nh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var sg = System.Drawing.Graphics.FromImage(small))
                {
                    sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    sg.DrawImage(bmp, 0, 0, targetMaxW, nh);
                }
                toEncode = small;
                scaled = true;
            }

            try
            {
                using var ms = new MemoryStream();
                var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                if (encoder is not null)
                {
                    using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 20L, 95L));
                    toEncode.Save(ms, encoder, ep);
                }
                else
                {
                    toEncode.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                return Task.FromResult<string?>("data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()));
            }
            finally
            {
                if (scaled) toEncode.Dispose();
            }
        }
        catch (Exception ex)
        {
            CallLogger.Error($"GDI capture failed src={src.Kind} {src.Width}x{src.Height} handle={src.Handle}", ex);
            return Task.FromResult<string?>(null);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diNormal);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    private static void DrawCursorGdi(System.Drawing.Graphics g, int captureLeft, int captureTop)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;
            if (!GetIconInfo(ci.hCursor, out var ii)) return;
            try
            {
                int x = ci.ptScreenPos.X - captureLeft - ii.xHotspot;
                int y = ci.ptScreenPos.Y - captureTop - ii.yHotspot;
                IntPtr hdc = g.GetHdc();
                try { DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
                finally { g.ReleaseHdc(hdc); }
            }
            finally
            {
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
        }
        catch { }
    }

    private void Finish(string text, string? callId)
    {
        CallInfo? call;
        lock (_lock) call = _current;
        if (callId is not null && call is not null && !SameCall(call.CallId, callId))
        {
            CallLogger.Info($"Finish ignored for {callId}, current={call.CallId}");
            return;
        }
        lock (_lock)
        {
            _current = null;
            _localSignalsReady = false;
            _pendingRemoteSignals.Clear();
        }
        IsScreenSharing = false;
        try { _screenCaptureCts?.Cancel(); } catch { }
        _screenCaptureCts?.Dispose();
        _screenCaptureCts = null;
        _screenCaptureTask = null;
        _wgcCapturer?.Dispose();
        _wgcCapturer = null;
        _pendingScreenSource = null;
        _ = ExecJsAsync("window.gopingCall && window.gopingCall.stop()");
        Emit(CallStatus.Ended, text);
        void Clear() => RemoteVideoCleared?.Invoke();
        if (Application.Current?.Dispatcher?.CheckAccess() == true) Clear();
        else Application.Current?.Dispatcher?.BeginInvoke(Clear);
    }

    // Called from JS bridge
    internal void OnBridgeStatus(string text)
    {
        CallLogger.Info($"bridge status: {text} callId={_current?.CallId}");
        CallInfo? call;
        lock (_lock) call = _current;
        if (call is null) return;
        if (string.Equals(text, "Connected", StringComparison.OrdinalIgnoreCase))
            Emit(CallStatus.Active, "Connected");
        else if (string.Equals(text, "Screen sharing", StringComparison.OrdinalIgnoreCase))
            Emit(CallStatus.Active, "Screen sharing");
        else if (string.Equals(text, "Reconnecting…", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Reconnecting...", StringComparison.OrdinalIgnoreCase))
            Emit(CallStatus.Active, "Reconnecting…");
        else if (string.Equals(text, "Ended", StringComparison.OrdinalIgnoreCase))
        { /* stop() already handled */ }
        else if (_state == CallStatus.Active || _state == CallStatus.Outgoing)
            Emit(_state, text);
    }

    internal void OnBridgeSignal(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            JsonElement data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            CallInfo? call;
            lock (_lock) call = _current;
            if (call is null) return;

            if (string.Equals(kind, "screen-stop", StringComparison.OrdinalIgnoreCase))
            {
                IsScreenSharing = false;
                void Clear() => RemoteVideoCleared?.Invoke();
                if (Application.Current?.Dispatcher?.CheckAccess() == true) Clear();
                else Application.Current?.Dispatcher?.BeginInvoke(Clear);
                // still forward to peer so their UI clears immediately
            }

            // Forward to peer via WS signaling
            object payload = new { type = "call.signal", callId = call.CallId, chatId = call.ChatId, kind, data };
            _ = SendAsync(payload);
        }
        catch (Exception ex)
        {
            CallLogger.Error("bridge signal parse failed", ex);
        }
    }

    internal void OnBridgeSpeaking(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            bool local = doc.RootElement.TryGetProperty("local", out var l) && l.GetBoolean();
            bool remote = doc.RootElement.TryGetProperty("remote", out var r) && r.GetBoolean();
            void Raise() => SpeakingChanged?.Invoke(local, remote);
            if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
            else Application.Current?.Dispatcher?.BeginInvoke(Raise);
        }
        catch { }
    }

    internal void OnBridgeStats(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int? rtt = root.TryGetProperty("rttMs", out var rttEl) && rttEl.ValueKind == JsonValueKind.Number ? rttEl.GetInt32() : null;
            double? jitter = root.TryGetProperty("jitterMs", out var jEl) && jEl.ValueKind == JsonValueKind.Number ? jEl.GetDouble() : null;
            int? lost = root.TryGetProperty("packetsLost", out var plEl) && plEl.ValueKind == JsonValueKind.Number ? plEl.GetInt32() : null;
            int? recv = root.TryGetProperty("packetsReceived", out var prEl) && prEl.ValueKind == JsonValueKind.Number ? prEl.GetInt32() : null;
            string? codec = root.TryGetProperty("codec", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;

            var history = new List<CallJitterPoint>();
            if (root.TryGetProperty("history", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hEl.EnumerateArray())
                {
                    long t = item.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.Number ? tEl.GetInt64() : 0;
                    double j = item.TryGetProperty("jitter", out var hjEl) && hjEl.ValueKind == JsonValueKind.Number ? hjEl.GetDouble() : 0;
                    int? hr = item.TryGetProperty("rtt", out var hrEl) && hrEl.ValueKind == JsonValueKind.Number ? hrEl.GetInt32() : null;
                    history.Add(new CallJitterPoint(t, j, hr));
                }
            }

            var snap = new CallStatsSnapshot(rtt, jitter, lost, recv, codec, history);
            void Raise() => StatsUpdated?.Invoke(snap);
            if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
            else Application.Current?.Dispatcher?.BeginInvoke(Raise);
        }
        catch (Exception ex)
        {
            CallLogger.Error("bridge stats parse failed", ex);
        }
    }

    internal void OnBridgeScreenState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool sharing = root.TryGetProperty("sharing", out var sEl) && sEl.GetBoolean();
            string? error = root.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
            IsScreenSharing = sharing;
            if (!sharing) _pendingScreenSource = null;
            CallLogger.Info($"bridge screenState sharing={sharing} error={error}");
            void Raise() => ScreenShareStateChanged?.Invoke(sharing, error);
            if (Application.Current?.Dispatcher?.CheckAccess() == true) Raise();
            else Application.Current?.Dispatcher?.BeginInvoke(Raise);
        }
        catch (Exception ex)
        {
            CallLogger.Error("bridge screenState parse failed", ex);
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class CallBridge
{
    private readonly CallService _svc;
    public CallBridge(CallService svc) { _svc = svc; }

    public void status(string text) => _svc.OnBridgeStatus(text);
    public void signal(string json) => _svc.OnBridgeSignal(json);
    public void speaking(string json) => _svc.OnBridgeSpeaking(json);
    public void stats(string json) => _svc.OnBridgeStats(json);
    public void screenState(string json) => _svc.OnBridgeScreenState(json);
}

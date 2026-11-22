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
        if (_webView?.CoreWebView2 is null) return Task.CompletedTask;
        if (_webView.Dispatcher.CheckAccess())
            return _webView.CoreWebView2.ExecuteScriptAsync(script);
        return _webView.Dispatcher.InvokeAsync(() => _webView.CoreWebView2.ExecuteScriptAsync(script)).Task.Unwrap();
    }

    private Task PostWebMessageAsync(object payload)
    {
        if (_webView?.CoreWebView2 is null) return Task.CompletedTask;
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        if (_webView.Dispatcher.CheckAccess())
        {
            _webView.CoreWebView2.PostWebMessageAsJson(json);
            return Task.CompletedTask;
        }
        return _webView.Dispatcher.InvokeAsync(() => _webView.CoreWebView2.PostWebMessageAsJson(json)).Task;
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
        _pendingScreenSource = source;
        await EnsureEngineReadyAsync();
        await StopScreenCaptureLoopAsync(stopJs: false);
        // Use WebView2 getDisplayMedia (reliable) with in-WebView gesture prompt.
        _ = ExecJsAsync("window.gopingCall && window.gopingCall.requestScreenShare()");
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
        if (stopJs)
            await ExecJsAsync("window.gopingCall && window.gopingCall.stopScreenShare().catch(e=>console.error(e))");
    }

    private async Task ScreenCaptureLoopAsync(ScreenShareSource src, CancellationToken ct)
    {
        int sent = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? dataUrl = CaptureFrameDataUrl(src);
                if (dataUrl is not null)
                {
                    await PostWebMessageAsync(new { type = "screenFrame", dataUrl });
                    sent++;
                    if (sent == 1 || sent % 50 == 0)
                        CallLogger.Info($"screen frame sent #{sent} len={dataUrl.Length} src={src.Kind} {src.Width}x{src.Height}");
                }
                else if (sent == 0)
                {
                    CallLogger.Info("screen capture returned null frame");
                }
            }
            catch (Exception ex)
            {
                CallLogger.Error("screen capture frame failed", ex);
            }

            try { await Task.Delay(100, ct); } catch { break; }
        }
    }

    private static string? CaptureFrameDataUrl(ScreenShareSource src)
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
                if (src.Kind == ScreenShareKind.Monitor)
                {
                    g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy | System.Drawing.CopyPixelOperation.CaptureBlt);
                }
                else
                {
                    bool captured = false;
                    try
                    {
                        g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h),
                            System.Drawing.CopyPixelOperation.SourceCopy | System.Drawing.CopyPixelOperation.CaptureBlt);
                        captured = !IsMostlyBlack(bmp);
                    }
                    catch { }

                    if (!captured)
                    {
                        IntPtr hdc = g.GetHdc();
                        try
                        {
                            PrintWindow(src.Handle, hdc, 2);
                        }
                        finally
                        {
                            if (hdc != IntPtr.Zero) g.ReleaseHdc(hdc);
                        }
                    }
                }
            }

            // Scale down large captures to reduce payload size.
            const int maxW = 960;
            System.Drawing.Bitmap toEncode = bmp;
            bool scaled = false;
            if (w > maxW)
            {
                int nh = Math.Max(1, (int)(h * (maxW / (double)w)));
                var small = new System.Drawing.Bitmap(maxW, nh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var sg = System.Drawing.Graphics.FromImage(small))
                {
                    sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    sg.DrawImage(bmp, 0, 0, maxW, nh);
                }
                toEncode = small;
                scaled = true;
            }

            try
            {
                using var ms = new MemoryStream();
                var encoder = GetJpegEncoder();
                if (encoder is not null)
                {
                    using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 45L);
                    toEncode.Save(ms, encoder, ep);
                }
                else
                {
                    toEncode.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            finally
            {
                if (scaled) toEncode.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMostlyBlack(System.Drawing.Bitmap bmp)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            int stepX = Math.Max(1, w / 20);
            int stepY = Math.Max(1, h / 20);
            int samples = 0, bright = 0;
            for (int y = 0; y < h; y += stepY)
            {
                for (int x = 0; x < w; x += stepX)
                {
                    var c = bmp.GetPixel(x, y);
                    samples++;
                    if (c.R > 15 || c.G > 15 || c.B > 15) bright++;
                }
            }
            return samples == 0 || (bright * 100 / samples) < 2;
        }
        catch { return false; }
    }

    private static System.Drawing.Imaging.ImageCodecInfo? GetJpegEncoder()
    {
        return System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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

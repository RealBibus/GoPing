using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace GoPing.Services;

public enum RealtimeStatus
{
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Держит авторизованное WebSocket-соединение и передаёт входящие JSON-события
/// владельцу. При разрыве сам переподключается; после пяти неудач сообщает об этом.
/// </summary>
public sealed class RealtimeClient : IDisposable
{
    private const int MaxFailedAttempts = 5;

    private readonly ApiClient _api;
    private readonly Func<string, Task> _onEvent;
    private readonly Action<RealtimeStatus, int>? _onStatus;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<string> _pendingCallSignals = new();
    private CancellationTokenSource _retrySignal = new();
    private readonly ManualResetEventSlim _resumeSignal = new(true);
    private volatile bool _paused;
    private ClientWebSocket? _socket;
    private Task? _runner;
    private int _started;
    private int _failedAttempts;
    private bool _failureReported;

    public RealtimeClient(
        ApiClient api,
        Func<string, Task> onEvent,
        Action<RealtimeStatus, int>? onStatus = null)
    {
        _api = api;
        _onEvent = onEvent;
        _onStatus = onStatus;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _runner = Task.Run(RunAsync);
    }

    public void Reconnect()
    {
        CancellationTokenSource previous = Interlocked.Exchange(ref _retrySignal, new CancellationTokenSource());
        previous.Cancel();

        _failedAttempts = 0;
        _failureReported = false;
        try { _socket?.Abort(); } catch { }
    }

    public void Pause()
    {
        _paused = true;
        _resumeSignal.Reset();
        try { _socket?.Abort(); } catch { }
    }

    public void Resume()
    {
        _paused = false;
        _failedAttempts = 0;
        _failureReported = false;
        _resumeSignal.Set();
        Reconnect();
    }

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        ClientWebSocket? socket = _socket;
        bool isCallSignal = json.Contains("\"type\":\"call.", StringComparison.Ordinal);

        if (socket is null || socket.State != WebSocketState.Open)
        {
            if (isCallSignal)
            {
                _pendingCallSignals.Enqueue(json);
                DebugLog($"queue call signal (ws not open): {TrimForLog(json)}");
            }
            return;
        }

        try
        {
            await SendFrameAsync(socket, json, ct).ConfigureAwait(false);
            if (isCallSignal) DebugLog($"sent call signal: {TrimForLog(json)}");
        }
        catch (Exception ex) when (isCallSignal && !ct.IsCancellationRequested)
        {
            _pendingCallSignals.Enqueue(json);
            DebugLog($"queue call signal after send error: {ex.Message} :: {TrimForLog(json)}");
        }
    }

    private static void DebugLog(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoPing");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "realtime-debug.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static string TrimForLog(string json)
        => json.Length <= 180 ? json : json[..180] + "...";

    /// <summary>Отправляет накопленные во время переподключения сигналы звонка.</summary>
    public async Task FlushPendingAsync(CancellationToken ct = default)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        while (_pendingCallSignals.TryDequeue(out string? json))
        {
            try
            {
                await SendFrameAsync(socket, json, ct).ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                _pendingCallSignals.Enqueue(json);
                return;
            }
        }
    }

    /// <summary>Удаляет осаждённые сигналы уже завершённого звонка.</summary>
    public void DropPendingSignals(string callId)
    {
        if (string.IsNullOrEmpty(callId)) return;
        int count = _pendingCallSignals.Count;
        for (int i = 0; i < count; i++)
        {
            if (!_pendingCallSignals.TryDequeue(out string? json)) break;
            if (!json.Contains(callId, StringComparison.Ordinal)) _pendingCallSignals.Enqueue(json);
        }
    }

    private static Task SendFrameAsync(ClientWebSocket socket, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task RunAsync()
    {
        CancellationToken token = _lifetime.Token;
        int delayMs = 1000;

        while (!token.IsCancellationRequested)
        {
            if (_paused)
            {
                try { _resumeSignal.Wait(token); } catch (OperationCanceledException) { break; }
                continue;
            }

            ClientWebSocket? socket = null;
            try
            {
                string accessToken = await _api.GetAccessTokenAsync(token).ConfigureAwait(false);
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _socket = socket;

                await socket.ConnectAsync(_api.BuildWebSocketUri(accessToken), token).ConfigureAwait(false);
                delayMs = 1000;
                _failedAttempts = 0;
                _failureReported = false;
                Report(RealtimeStatus.Connected);

                // Очередь и rejoin от MainWindow могут появиться сразу после Connected.
                await FlushPendingAsync(token).ConfigureAwait(false);
                await Task.Delay(50, token).ConfigureAwait(false);
                await FlushPendingAsync(token).ConfigureAwait(false);

                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Сеть может быть недоступна — попробуем ещё раз ниже.
            }
            finally
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                socket?.Dispose();
            }

            if (token.IsCancellationRequested) break;
            if (_paused)
            {
                try { _resumeSignal.Wait(token); } catch (OperationCanceledException) { break; }
                delayMs = 1000;
                continue;
            }

            _failedAttempts++;
            if (_failedAttempts >= MaxFailedAttempts)
            {
                if (!_failureReported)
                {
                    Report(RealtimeStatus.Failed, _failedAttempts);
                    _failureReported = true;
                }
            }
            else
            {
                Report(RealtimeStatus.Reconnecting, _failedAttempts);
            }

            CancellationTokenSource signal = Volatile.Read(ref _retrySignal);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, signal.Token);
                await Task.Delay(delayMs, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Retry из окна ошибки — начинаем сразу, без накопленной задержки.
                delayMs = 1000;
                continue;
            }
            delayMs = Math.Min(delayMs * 2, 10_000);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                payload.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            string json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
            try
            {
                await _onEvent(json).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ошибка обработки одного события не должна рвать соединение.
            }
        }
    }

    private void Report(RealtimeStatus status, int attempt = 0)
    {
        if (_onStatus is null) return;
        try
        {
            _onStatus(status, attempt);
        }
        catch
        {
            // Состояние соединения не должно влиять на саму переподключку.
        }
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        try { _retrySignal.Cancel(); } catch { }
        try { _socket?.Abort(); } catch { }
        _pendingCallSignals.Clear();
        _lifetime.Dispose();
    }
}

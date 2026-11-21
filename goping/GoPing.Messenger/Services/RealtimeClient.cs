using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace GoPing.Services;

/// <summary>
/// Держит авторизованное WebSocket-соединение и передаёт входящие JSON-события
/// владельцу. При разрыве сам переподключается с увеличивающейся задержкой.
/// </summary>
public sealed class RealtimeClient : IDisposable
{
    private readonly ApiClient _api;
    private readonly Func<string, Task> _onEvent;
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket? _socket;
    private Task? _runner;
    private int _started;

    public RealtimeClient(ApiClient api, Func<string, Task> onEvent)
    {
        _api = api;
        _onEvent = onEvent;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _runner = Task.Run(RunAsync);
    }

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        CancellationToken token = _lifetime.Token;
        int delayMs = 1000;

        while (!token.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                string accessToken = await _api.GetAccessTokenAsync(token).ConfigureAwait(false);
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _socket = socket;

                await socket.ConnectAsync(_api.BuildWebSocketUri(accessToken), token).ConfigureAwait(false);
                delayMs = 1000;
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
            try
            {
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        try { _socket?.Abort(); } catch { }
        _lifetime.Dispose();
    }
}

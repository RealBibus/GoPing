using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GoPing.Models;

namespace GoPing.Services;

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string DefaultBaseUrl = "http://26.68.224.240:8080";

    private readonly HttpClient _http;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _expiresAt;

    public string BaseUrl { get; private set; }
    public string? RefreshToken => _refreshToken;

    /// <summary>Returns a current access token, refreshing it when needed.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return _accessToken
            ?? throw new ApiException("NO_ACCESS_TOKEN", "Sign in again to continue synchronization.");
    }

    /// <summary>Builds the authenticated WebSocket endpoint from the configured API URL.</summary>
    public Uri BuildWebSocketUri(string accessToken)
    {
        Uri baseUri = new(BaseUrl);
        string scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        string authority = baseUri.GetComponents(UriComponents.HostAndPort, UriFormat.Unescaped);
        return new Uri($"{scheme}://{authority}/api/v1/ws?token={Uri.EscapeDataString(accessToken)}");
    }

    public ApiClient(string? baseUrl = null)
    {
        string? configured = baseUrl ?? Environment.GetEnvironmentVariable("GOPING_API_URL");
        BaseUrl = string.IsNullOrWhiteSpace(configured)
            ? DefaultBaseUrl
            : configured.TrimEnd('/');

        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GoPing-Client/1.0");
    }

    /// <summary>Switches server at runtime; the WebSocket builder follows BaseUrl.</summary>
    public void SetBaseUrl(string baseUrl)
    {
        string normalized = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized)) normalized = DefaultBaseUrl;
        BaseUrl = normalized;
        _http.BaseAddress = new Uri(BaseUrl + "/");
    }

    public void SetTokens(AuthResult auth)
    {
        _accessToken = auth.AccessToken;
        _refreshToken = auth.RefreshToken;
        _expiresAt = auth.ExpiresAt;
    }

    /// <summary>Восстанавливает сохранённую сессию: access-токен будет получен через refresh.</summary>
    public void UseRefreshToken(string refreshToken)
    {
        _accessToken = null;
        _refreshToken = refreshToken;
        // Срок действия access-токена неизвестен — следующий запрос обновит его.
        _expiresAt = null;
    }

    public async Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken ct = default)
    {
        AuthResult auth = await SendPublicAsync<AuthResult>(
            HttpMethod.Post, "api/v1/auth/token", new { token }, ct);
        SetTokens(auth);
        return auth;
    }

    public async Task<AuthResult> RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            throw new ApiException("INVALID_REFRESH_TOKEN", "Your session expired. Sign in again.");
        }

        AuthResult auth = await SendPublicAsync<AuthResult>(
            HttpMethod.Post, "api/v1/auth/refresh", new { refreshToken = _refreshToken }, ct);
        SetTokens(auth);
        return auth;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        string? refresh = _refreshToken;
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = null;
        if (string.IsNullOrWhiteSpace(refresh))
        {
            return;
        }
        try
        {
            await SendPublicAsync<JsonElement>(
                HttpMethod.Post, "api/v1/auth/logout", new { refreshToken = refresh }, ct);
        }
        catch
        {
            // Выход на клиенте важнее ответа сервера.
        }
    }

    public Task<GithubStart> StartGithubAsync(CancellationToken ct = default)
        => SendPublicAsync<GithubStart>(HttpMethod.Get, "api/v1/auth/github/start", null, ct);

    public async Task<byte[]> DownloadAvatarAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<GithubPoll> PollGithubAsync(string state, CancellationToken ct = default)
    {
        GithubPoll poll = await SendPublicAsync<GithubPoll>(
            HttpMethod.Get,
            "api/v1/auth/github/poll?state=" + Uri.EscapeDataString(state),
            null,
            ct);
        // Без этого MainWindow получает клиент без токенов и все запросы
        // отвечают 401, пока приложение не перезапустится с сохранённой сессией.
        if (poll.Auth is not null)
        {
            SetTokens(poll.Auth);
        }
        return poll;
    }

    public async Task<UserProfile> GetMeAsync(CancellationToken ct = default)
    {
        UserEnvelope envelope = await SendAuthenticatedAsync<UserEnvelope>(
            HttpMethod.Get, "api/v1/users/me", null, ct);
        return envelope.User;
    }

    public async Task<UserProfile> UpdateProfileAsync(
        string? nickname, string? username, string? bio, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>();
        if (nickname is not null) body["nickname"] = nickname;
        if (username is not null) body["username"] = username;
        if (bio is not null) body["bio"] = bio;

        UserEnvelope envelope = await SendAuthenticatedAsync<UserEnvelope>(
            HttpMethod.Patch, "api/v1/users/me", JsonContent.Create(body, options: JsonOptions), ct);
        return envelope.User;
    }

    public async Task<UserProfile> UploadAvatarAsync(string filePath, CancellationToken ct = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeFor(filePath));
        form.Add(file, "avatar", Path.GetFileName(filePath));

        UserEnvelope envelope = await SendAuthenticatedAsync<UserEnvelope>(
            HttpMethod.Post, "api/v1/users/me/avatar", form, ct);
        return envelope.User;
    }

    public async Task<UserProfile> DeleteAvatarAsync(CancellationToken ct = default)
    {
        UserEnvelope envelope = await SendAuthenticatedAsync<UserEnvelope>(
            HttpMethod.Delete, "api/v1/users/me/avatar", null, ct);
        return envelope.User;
    }

    public Task HeartbeatAsync(CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(HttpMethod.Post, "api/v1/presence/heartbeat", null, ct);

    public Task MarkReadAsync(string chatId, CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/read",
            null,
            ct);

    public Task<ChatListResponse> GetChatsAsync(CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatListResponse>(HttpMethod.Get, "api/v1/chats?limit=30", null, ct);

    public async Task<ClientConfig> GetClientConfigAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAuthenticatedAsync<ClientConfig>(
                HttpMethod.Get, "api/v1/client-config", null, ct);
        }
        catch (ApiException)
        {
            // Старый сервер не знает этот endpoint — разрешаем удаление по умолчанию.
            return new ClientConfig(true);
        }
    }

    /// <summary>ICE-серверы для WebRTC-звонков (STUN + при наличии coturn).</summary>
    public async Task<RtcConfigResponse> GetRtcConfigAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAuthenticatedAsync<RtcConfigResponse>(
                HttpMethod.Get, "api/v1/rtc/config", null, ct);
        }
        catch (ApiException)
        {
            // Старый сервер ещё не отдаёт TURN — хватит публичного STUN.
            return new RtcConfigResponse(null);
        }
    }

    /// <summary>Быстрый поиск пользователей по подстроке username.</summary>
    public async Task<IReadOnlyList<UserProfile>> SearchUsersAsync(string query, CancellationToken ct = default)
    {
        var response = await SendAuthenticatedAsync<UserSearchResponse>(
            HttpMethod.Get,
            "api/v1/users/search?q=" + Uri.EscapeDataString(query),
            null,
            ct);
        return response.Users ?? Array.Empty<UserProfile>();
    }

    /// <summary>Профиль пользователя по username (для личных чатов). </summary>
    public async Task<UserProfile> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        UserEnvelope envelope = await SendAuthenticatedAsync<UserEnvelope>(
            HttpMethod.Get,
            "api/v1/users/username/" + Uri.EscapeDataString(username),
            null,
            ct);
        return envelope.User;
    }

    /// <summary>Создаёт личный чат по username или возвращает уже существующий.</summary>
    public Task<ChatSummary> CreateChatAsync(string username, CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatSummary>(
            HttpMethod.Post,
            "api/v1/chats",
            JsonContent.Create(new { username }, options: JsonOptions),
            ct);

    public Task<MessageListResponse> GetMessagesAsync(string chatId, CancellationToken ct = default)
        => SendAuthenticatedAsync<MessageListResponse>(
            HttpMethod.Get,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages?limit=100",
            null,
            ct);

    /// <summary>All pinned messages of the chat, even older than the loaded window.</summary>
    public Task<MessageListResponse> GetPinnedMessagesAsync(string chatId, CancellationToken ct = default)
        => SendAuthenticatedAsync<MessageListResponse>(
            HttpMethod.Get,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/pins",
            null,
            ct);

    /// <summary>Sends a message; the server protects it before storing it.</summary>
    public Task<ChatMessage> SendMessageAsync(
        string chatId, string text, string? replyToId = null, CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatMessage>(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages",
            JsonContent.Create(new { text, replyToId }, options: JsonOptions),
            ct);

    public Task<ChatMessage> EditMessageAsync(
        string chatId, string messageId, string text, CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatMessage>(
            HttpMethod.Patch,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages/" + Uri.EscapeDataString(messageId),
            JsonContent.Create(new { text }, options: JsonOptions),
            ct);

    public Task<ChatMessage> ForwardMessageAsync(
        string chatId, string fromChatId, string messageId, CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatMessage>(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages/forward",
            JsonContent.Create(new { fromChatId, messageId }, options: JsonOptions),
            ct);

    public async Task<ChatMessage> SendVoiceAsync(
        string chatId, byte[] audioBytes, string fileName, string contentType, int durationSec, string? replyToId = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        // MediaRecorder отдаёт MIME вида "audio/webm;codecs=opus" — конструктор
        // MediaTypeHeaderValue такое не принимает, нужен парсер с параметрами.
        string mime = string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType;
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(mime, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "audio", string.IsNullOrWhiteSpace(fileName) ? "voice.webm" : fileName);
        form.Add(new StringContent(Math.Max(0, durationSec).ToString(System.Globalization.CultureInfo.InvariantCulture)), "durationSec");
        if (!string.IsNullOrWhiteSpace(replyToId))
            form.Add(new StringContent(replyToId), "replyToId");

        return await SendAuthenticatedAsync<ChatMessage>(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages/voice",
            form, ct);
    }

    public Task DeleteMessageAsync(
        string chatId, string messageId, bool forEveryone, CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(
            HttpMethod.Delete,
            "api/v1/chats/" + Uri.EscapeDataString(chatId)
                + "/messages/" + Uri.EscapeDataString(messageId),
            JsonContent.Create(new { forEveryone }, options: JsonOptions),
            ct);

    public Task PinMessageAsync(
        string chatId, string messageId, string scope, CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(
            HttpMethod.Post,
            PinPath(chatId, messageId),
            JsonContent.Create(new { scope }, options: JsonOptions),
            ct);

    public Task UnpinMessageAsync(
        string chatId, string messageId, string scope, CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(
            HttpMethod.Delete,
            PinPath(chatId, messageId),
            JsonContent.Create(new { scope }, options: JsonOptions),
            ct);

    private static string PinPath(string chatId, string messageId)
        => "api/v1/chats/" + Uri.EscapeDataString(chatId)
            + "/messages/" + Uri.EscapeDataString(messageId) + "/pin";

    /// <summary>Notifies the server that the local user is typing in a chat.</summary>
    public Task SendTypingAsync(string chatId, CancellationToken ct = default)
        => SendAuthenticatedEmptyAsync(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/typing",
            null,
            ct);

    /// <summary>Reads server limits, falling back to safe defaults on older servers.</summary>
    public async Task<LimitsConfig> GetLimitsAsync(CancellationToken ct = default)
    {
        LimitsConfig defaults = LimitsConfig.Default;
        try
        {
            JsonElement root = await SendAuthenticatedAsync<JsonElement>(
                HttpMethod.Get, "api/v1/client-config", null, ct);
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("limits", out JsonElement limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                return defaults;
            }

            return new LimitsConfig(
                ReadInt(limits, "maxTextLength", defaults.MaxTextLength),
                ReadInt(limits, "maxVoiceSeconds", defaults.MaxVoiceSeconds),
                ReadInt(limits, "maxCallMinutes", defaults.MaxCallMinutes),
                ReadLong(limits, "maxFileBytes", defaults.MaxFileBytes));
        }
        catch (ApiException)
        {
            return defaults;
        }
    }

    public Task<PollDto> CreatePollAsync(
        string chatId, string question, IReadOnlyList<string> options, CancellationToken ct = default)
        => SendAuthenticatedAsync<PollDto>(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/polls",
            JsonContent.Create(new { question, options }, options: JsonOptions),
            ct);

    public Task<PollDto> VotePollAsync(
        string chatId, string pollId, string optionId, CancellationToken ct = default)
        => SendAuthenticatedAsync<PollDto>(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/polls/"
                + Uri.EscapeDataString(pollId) + "/vote",
            JsonContent.Create(new { optionId }, options: JsonOptions),
            ct);

    /// <summary>Streams a media file to the server and reports 0 to 100 percent.</summary>
    public async Task UploadMediaAsync(
        string chatId,
        Stream data,
        string fileName,
        string mime,
        string kind,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        using var form = new MultipartFormDataContent();
        var progressStream = new ProgressReportingStream(data, progress);
        using var file = new StreamContent(progressStream);
        string requestedMime = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(requestedMime, out MediaTypeHeaderValue? parsed)
            ? parsed
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : fileName);
        form.Add(new StringContent(string.IsNullOrWhiteSpace(kind) ? "file" : kind), "kind");

        await SendAuthenticatedEmptyAsync(
            HttpMethod.Post,
            "api/v1/chats/" + Uri.EscapeDataString(chatId) + "/messages/media",
            form,
            ct);
        progress?.Report(100);
    }

    /// <summary>Issues an admin token using the shared admin secret.</summary>
    public async Task<string> IssueAdminTokenAsync(
        string adminSecret, string? label, CancellationToken ct = default)
    {
        JsonElement response = await SendAdminAsync<JsonElement>(
            HttpMethod.Post,
            "api/v1/admin/tokens",
            JsonContent.Create(new { label }, options: JsonOptions),
            adminSecret,
            ct);
        string? token = ReadText(response, "token") ?? ReadText(response, "value");
        if (string.IsNullOrWhiteSpace(token))
            throw new ApiException("EMPTY_RESPONSE", "The admin endpoint did not return a token.");
        return token!;
    }

    /// <summary>Reads recent server log lines using the shared admin secret.</summary>
    public async Task<IReadOnlyList<string>> GetAdminLogsAsync(
        string adminSecret, int lines = 200, CancellationToken ct = default)
    {
        int count = Math.Clamp(lines, 1, 10000);
        JsonElement response = await SendAdminAsync<JsonElement>(
            HttpMethod.Get,
            "api/v1/admin/logs?lines=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            adminSecret,
            ct);

        JsonElement array = response;
        if (response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("lines", out JsonElement found)
            && found.ValueKind == JsonValueKind.Array)
        {
            array = found;
        }
        var result = new List<string>();
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    result.Add(item.GetString() ?? string.Empty);
            }
        }
        return result;
    }

    public Task UpdateLimitsAsync(
        string adminSecret, LimitsConfig limits, CancellationToken ct = default)
        => SendAdminEmptyAsync(
            HttpMethod.Post,
            "api/v1/admin/limits",
            JsonContent.Create(new
            {
                maxTextLength = limits.MaxTextLength,
                maxVoiceSeconds = limits.MaxVoiceSeconds,
                maxCallMinutes = limits.MaxCallMinutes,
                maxFileBytes = limits.MaxFileBytes
            }, options: JsonOptions),
            adminSecret,
            ct);

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
        {
            return;
        }
        // Складываем вместо вычитания: AddSeconds(-60) переполняется,
        // если expiresAt когда-либо окажется DateTimeOffset.MinValue.
        if (_expiresAt is not null && DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60) < _expiresAt.Value)
        {
            return;
        }
        await RefreshAsync(ct);
    }

    private async Task<T> SendPublicAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        T? result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new ApiException("EMPTY_RESPONSE", "The server returned an empty response.");
    }

    private async Task<T> SendAuthenticatedAsync<T>(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (_accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        T? result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new ApiException("EMPTY_RESPONSE", "The server returned an empty response.");
    }

    private async Task SendAuthenticatedEmptyAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (_accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<T> SendAdminAsync<T>(
        HttpMethod method, string path, HttpContent? content, string adminSecret, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Admin-Secret", adminSecret ?? string.Empty);
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        T? result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new ApiException("EMPTY_RESPONSE", "The server returned an empty response.");
    }

    private async Task SendAdminEmptyAsync(
        HttpMethod method, string path, HttpContent? content, string adminSecret, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Admin-Secret", adminSecret ?? string.Empty);
        using HttpResponseMessage response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    private static long ReadLong(JsonElement element, string name, long fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
            ? parsed
            : fallback;

    private static string? ReadText(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("TIMEOUT", "The server did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("NETWORK",
                $"Could not reach {BaseUrl}. Is the server running? {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string text = await response.Content.ReadAsStringAsync(ct);
        string code = response.StatusCode.ToString();
        string message = $"The server responded with {(int)response.StatusCode}.";
        try
        {
            ApiErrorEnvelope? envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(text, JsonOptions);
            if (envelope?.Error is not null)
            {
                if (!string.IsNullOrWhiteSpace(envelope.Error.Code))
                {
                    code = envelope.Error.Code;
                }
                if (!string.IsNullOrWhiteSpace(envelope.Error.Message))
                {
                    message = envelope.Error.Message;
                }
            }
        }
        catch (JsonException)
        {
            // Не JSON — оставляем общий текст.
        }

        throw new ApiException(code, message, (HttpStatusCode)response.StatusCode);
    }

    private static string MediaTypeFor(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    /// <summary>Counts bytes read by StreamContent so upload progress can be reported.</summary>
    private sealed class ProgressReportingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IProgress<double>? _progress;
        private readonly long _total;
        private long _read;

        public ProgressReportingStream(Stream inner, IProgress<double>? progress)
        {
            _inner = inner;
            _progress = progress;
            _total = inner.CanSeek ? Math.Max(0, inner.Length - inner.Position) : 0;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, ct);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int read = await _inner.ReadAsync(buffer, ct);
            Report(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        private void Report(int count)
        {
            if (count <= 0 || _progress is null) return;
            _read += count;
            double value = _total > 0 ? Math.Min(100, _read * 100.0 / _total) : 0;
            _progress.Report(value);
        }
    }

    public void Dispose() => _http.Dispose();
}

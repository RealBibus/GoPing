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

    private readonly HttpClient _http;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _expiresAt;

    public string BaseUrl { get; }
    public string? RefreshToken => _refreshToken;

    public ApiClient(string? baseUrl = null)
    {
        string? configured = baseUrl ?? Environment.GetEnvironmentVariable("GOPING_API_URL");
        BaseUrl = string.IsNullOrWhiteSpace(configured)
            ? "http://localhost:8080"
            : configured.TrimEnd('/');

        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GoPing-Client/1.0");
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
        _expiresAt = DateTimeOffset.MinValue;
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

    public Task<GithubPoll> PollGithubAsync(string state, CancellationToken ct = default)
        => SendPublicAsync<GithubPoll>(
            HttpMethod.Get,
            "api/v1/auth/github/poll?state=" + Uri.EscapeDataString(state),
            null,
            ct);

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

    public Task<ChatListResponse> GetChatsAsync(CancellationToken ct = default)
        => SendAuthenticatedAsync<ChatListResponse>(HttpMethod.Get, "api/v1/chats?limit=30", null, ct);

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
        {
            return;
        }
        if (_expiresAt is not null && DateTimeOffset.UtcNow < _expiresAt.Value.AddSeconds(-60))
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

    public void Dispose() => _http.Dispose();
}

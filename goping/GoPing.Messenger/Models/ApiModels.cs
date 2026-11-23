using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GoPing.Models;

public sealed record UserProfile(
    string Id,
    string Username,
    string Nickname,
    string Bio,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Online = false,
    DateTimeOffset? LastSeenAt = null);

public sealed record AuthResult(
    UserProfile User,
    string TokenType,
    string AccessToken,
    string RefreshToken,
    long ExpiresIn,
    DateTimeOffset ExpiresAt);

public sealed record UserEnvelope(UserProfile User);

public sealed record UserSearchResponse(IReadOnlyList<UserProfile> Users);

public sealed record GithubStart(
    string AuthorizeUrl,
    string State,
    DateTimeOffset ExpiresAt);

public sealed record GithubPoll(
    string Status,
    AuthResult? Auth);

public sealed record ServerEntry(string Id, string Name, string Url);

public sealed record LimitsConfig(
    int MaxTextLength,
    int MaxVoiceSeconds,
    int MaxCallMinutes,
    long MaxFileBytes)
{
    public static LimitsConfig Default { get; } = new(4000, 300, 0, 52428800);
}

public sealed record PollOption(string Id, string Text, int Votes);

public sealed record PollDto(
    string Id,
    string Question,
    IReadOnlyList<PollOption> Options,
    int TotalVotes,
    bool Closed,
    string? MyVoteOptionId)
{
    /// <summary>Set by servers that report the vote flag separately from the option id.</summary>
    public bool VotedOptionIdSet { get; init; }

    public PollDto(
        string id,
        string question,
        IReadOnlyList<PollOption> options,
        int totalVotes,
        bool closed,
        bool votedOptionIdSet,
        string? myVoteOptionId)
        : this(id, question, options, totalVotes, closed, myVoteOptionId)
    {
        VotedOptionIdSet = votedOptionIdSet;
    }
}

/// <summary>Lightweight poll payload parsed from a chat message text.</summary>
public sealed record ParsedPoll(
    string PollId,
    string Question,
    IReadOnlyList<string> Options,
    IReadOnlyList<PollOption> PollOptions);

public sealed record MediaPayload(
    string Url,
    string FileName,
    long SizeBytes,
    string Mime,
    string Kind);

public sealed record MediaAttachment(string Url, string Kind, string FileName, long SizeBytes);

public sealed record ChatSummary(
    string Id,
    string Kind,
    string Title,
    string? Username = null,
    string? AvatarUrl = null,
    DateTimeOffset CreatedAt = default,
    string? PeerUserId = null,
    string? LastMessageText = null,
    DateTimeOffset? LastMessageAt = null,
    bool PeerOnline = false,
    DateTimeOffset? PeerLastSeenAt = null,
    bool PeerTyping = false,
    int UnreadCount = 0)
{
    public bool IsFavorites => string.Equals(Kind, "favorites", StringComparison.Ordinal);

    public string PreviewLine =>
        string.IsNullOrWhiteSpace(LastMessageText) ? "No messages yet" : LastMessageText!;
}

public sealed record ChatListResponse(
    IReadOnlyList<ChatSummary> Items,
    string? NextCursor);

public sealed record ChatMessageSender(
    string Id,
    string Username,
    string Nickname,
    string? AvatarUrl = null);

public sealed record ReplyPreview(
    string Id,
    string Text,
    string SenderNickname);

public sealed record VoicePayload(string Url, int DurationSec);

public sealed record ChatMessage(
    string Id,
    string ChatId,
    ChatMessageSender Sender,
    string Text,
    string Algorithm,
    DateTimeOffset CreatedAt,
    ReplyPreview? ReplyTo = null,
    string? Status = null,
    string? PinScope = null,
    string? Kind = null,
    DateTimeOffset? EditedAt = null,
    string? ForwardedFromSenderNickname = null,
    string? ForwardedFromText = null,
    bool Listened = false,
    double PlaybackSpeed = 1.0,
    string? PollJson = null,
    string? MediaJson = null)
{
    public bool IsCall => string.Equals(Kind, "call", StringComparison.OrdinalIgnoreCase);
    public bool IsVoice => string.Equals(Kind, "voice", StringComparison.OrdinalIgnoreCase);
    public bool IsForwarded => !string.IsNullOrWhiteSpace(ForwardedFromSenderNickname) || !string.IsNullOrWhiteSpace(ForwardedFromText);

    public VoicePayload? ParseVoice()
    {
        if (!IsVoice || string.IsNullOrWhiteSpace(Text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(Text);
            var root = doc.RootElement;
            string? url = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()
                : root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            int sec = root.TryGetProperty("durationSec", out var d) && d.TryGetInt32(out int v) ? Math.Max(0, v) : 0;
            if (string.IsNullOrWhiteSpace(url)) return null;
            return new VoicePayload(url, sec);
        }
        catch { return null; }
    }

    /// <summary>Reads poll metadata from the message text, accepting several server payload shapes.</summary>
    public ParsedPoll? ParsePoll()
    {
        string? json = PollJson;
        if (string.IsNullOrWhiteSpace(json)) json = Text;
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement data = root;
            if (root.TryGetProperty("poll", out var nested) && nested.ValueKind == JsonValueKind.Object) data = nested;
            else if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object) data = payload;

            string? pollId = ReadString(data, "pollId") ?? ReadString(data, "id");
            if (string.IsNullOrWhiteSpace(pollId)) return null;
            string question = ReadString(data, "question") ?? ReadString(data, "text") ?? string.Empty;

            var texts = new List<string>();
            var options = new List<PollOption>();
            if (data.TryGetProperty("options", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in array.EnumerateArray())
                {
                    index++;
                    string number = index.ToString(CultureInfo.InvariantCulture);
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string text = item.GetString() ?? string.Empty;
                        texts.Add(text);
                        options.Add(new PollOption(number, text, 0));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        string id = ReadString(item, "id") ?? number;
                        string text = ReadString(item, "text") ?? ReadString(item, "label") ?? id;
                        int votes = item.TryGetProperty("votes", out var v) && v.TryGetInt32(out int parsed) ? parsed : 0;
                        texts.Add(text);
                        options.Add(new PollOption(id, text, votes));
                    }
                }
            }

            return new ParsedPoll(pollId!, question, texts, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads media metadata from the message text, accepting path or url and raw strings.</summary>
    public MediaPayload? ParseMedia()
    {
        string? json = MediaJson;
        if (string.IsNullOrWhiteSpace(json)) json = Text;
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                string raw = root.GetString() ?? string.Empty;
                return string.IsNullOrWhiteSpace(raw) ? null : BuildMedia(raw, null, null, null, null);
            }
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement data = root;
            if (root.TryGetProperty("media", out var nested) && nested.ValueKind == JsonValueKind.Object) data = nested;
            else if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object) data = payload;

            string? url = ReadString(data, "path") ?? ReadString(data, "url");
            if (string.IsNullOrWhiteSpace(url)) return null;
            string? fileName = ReadString(data, "fileName") ?? ReadString(data, "filename");
            long? size = ReadInt64(data, "sizeBytes") ?? ReadInt64(data, "size");
            string? mime = ReadString(data, "mime") ?? ReadString(data, "contentType");
            string? kind = ReadString(data, "kind");
            return BuildMedia(url!, fileName, size, mime, kind);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaPayload BuildMedia(
        string url, string? fileName, long? sizeBytes, string? mime, string? kind)
    {
        string name = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(url.Split('?')[0]) is string candidate && !string.IsNullOrWhiteSpace(candidate)
                ? candidate
                : "media"
            : fileName!;
        string resolvedMime = string.IsNullOrWhiteSpace(mime) ? MimeForFileName(name) : mime!;
        string resolvedKind = string.IsNullOrWhiteSpace(kind) ? KindForMime(resolvedMime) : kind!;
        return new MediaPayload(url, name, Math.Max(0, sizeBytes ?? 0), resolvedMime, resolvedKind);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static string MimeForFileName(string name)
        => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };

    private static string KindForMime(string mime)
    {
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "photo";
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return "file";
    }
}

public sealed record MessageListResponse(
    IReadOnlyList<ChatMessage> Items,
    string? NextCursor);

public sealed record ClientConfig(bool AllowDeleteOthersMessages);

public sealed record RtcConfigResponse(IReadOnlyList<JsonElement>? IceServers);

public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    JsonElement? Details,
    string? RequestId);

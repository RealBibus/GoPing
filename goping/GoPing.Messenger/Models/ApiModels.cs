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
    bool PeerTyping = false)
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
    string? Kind = null)
{
    public bool IsCall => string.Equals(Kind, "call", StringComparison.OrdinalIgnoreCase);
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

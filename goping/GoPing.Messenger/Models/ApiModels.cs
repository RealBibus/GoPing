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
    public string PreviewLine =>
        PeerTyping
            ? "typing…"
            : (string.IsNullOrWhiteSpace(LastMessageText) ? "No messages yet" : LastMessageText!);
}

public sealed record ChatListResponse(
    IReadOnlyList<ChatSummary> Items,
    string? NextCursor);

public sealed record ChatMessageSender(
    string Id,
    string Username,
    string Nickname,
    string? AvatarUrl = null);

public sealed record ChatMessage(
    string Id,
    string ChatId,
    ChatMessageSender Sender,
    string Text,
    string Algorithm,
    DateTimeOffset CreatedAt);

public sealed record MessageListResponse(
    IReadOnlyList<ChatMessage> Items,
    string? NextCursor);

public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    JsonElement? Details,
    string? RequestId);

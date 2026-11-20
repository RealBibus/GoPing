using System.Text.Json;

namespace GoPing.Models;

public sealed record UserProfile(
    string Id,
    string Username,
    string Nickname,
    string Bio,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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
    DateTimeOffset CreatedAt);

public sealed record ChatListResponse(
    IReadOnlyList<ChatSummary> Items,
    string? NextCursor);

public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    JsonElement? Details,
    string? RequestId);

package com.goping.web.dto;

import java.time.Instant;
import java.util.List;

public final class ApiDtos {

    private ApiDtos() {
    }

    public record ServerTokenLoginRequest(String token) {
    }

    public record RefreshRequest(String refreshToken) {
    }

    public record UpdateProfileRequest(String nickname, String username, String bio) {
    }

    public record UserEnvelope(UserDto user) {
    }

    public record AuthResponse(
            UserDto user,
            String tokenType,
            String accessToken,
            String refreshToken,
            long expiresIn,
            Instant expiresAt
    ) {
    }

    public record UsernameAvailabilityResponse(String username, boolean available, String reason) {
    }

    public record UserSearchResponse(List<UserDto> users) {
    }

    public record GithubStartResponse(String authorizeUrl, String state, Instant expiresAt) {
    }

    public record GithubPollResponse(String status, AuthResponse auth) {
    }

    public record CreateServerTokenRequest(String label) {
    }

    public record AdminTokenDto(
            String id,
            String prefix,
            String label,
            String userId,
            Instant createdAt,
            Instant activatedAt,
            Instant lastUsedAt,
            Instant revokedAt
    ) {
    }

    public record AdminTokenIssued(AdminTokenDto token, String value) {
    }

    public record AdminTokenListResponse(List<AdminTokenDto> items) {
    }

    public record ChatDto(
            String id,
            String kind,
            String title,
            String username,
            String avatarUrl,
            Instant createdAt,
            String peerUserId,
            String lastMessageText,
            Instant lastMessageAt,
            boolean peerOnline,
            Instant peerLastSeenAt,
            boolean peerTyping
    ) {
    }

    public record ChatListResponse(List<ChatDto> items, String nextCursor) {
    }

    public record CreateChatRequest(String username) {
    }

    public record SendMessageRequest(String text, String replyToId) {
    }

    public record EditMessageRequest(String text) {
    }

    public record ForwardMessageRequest(String fromChatId, String messageId) {
    }

    public record DeleteMessageRequest(boolean forEveryone) {
    }

    /** scope: 'me' или 'chat'. */
    public record PinRequest(String scope) {
    }

    public record ClientConfigDto(boolean allowDeleteOthersMessages) {
    }

    public record MessageSenderDto(String id, String username, String nickname, String avatarUrl) {
    }

    public record ReplyPreviewDto(String id, String text, String senderNickname) {
    }

    public record MessageDto(
            String id,
            String chatId,
            MessageSenderDto sender,
            String text,
            String algorithm,
            String kind,
            Instant createdAt,
            ReplyPreviewDto replyTo,
            String status,
            String pinScope,
            Instant editedAt,
            String forwardedFromSenderNickname,
            String forwardedFromText
    ) {
    }

    public record MessageListResponse(List<MessageDto> items, String nextCursor) {
    }
}

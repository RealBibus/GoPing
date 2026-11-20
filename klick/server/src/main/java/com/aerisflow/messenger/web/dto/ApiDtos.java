package com.aerisflow.messenger.web.dto;

import java.util.List;

public final class ApiDtos {

    private ApiDtos() {
    }

    public record RegisterRequest(String email, String username, String password, String nickname) {
    }

    public record LoginRequest(String login, String password) {
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
            java.time.Instant expiresAt
    ) {
    }

    public record UsernameAvailabilityResponse(String username, boolean available, String reason) {
    }

    public record ChatDto(String id, String kind, String title, java.time.Instant createdAt) {
    }

    public record ChatListResponse(List<ChatDto> items, String nextCursor) {
    }
}

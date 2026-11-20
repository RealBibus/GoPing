package com.aerisflow.messenger.web.dto;

import com.fasterxml.jackson.annotation.JsonInclude;

import java.time.Instant;

/**
 * Пользователь в API. Email отдаётся только владельцу профиля.
 */
public record UserDto(
        String id,
        String username,
        String nickname,
        @JsonInclude(JsonInclude.Include.NON_NULL) String email,
        String bio,
        String avatarUrl,
        Instant createdAt,
        Instant updatedAt
) {
}

package com.goping.web.dto;

import java.time.Instant;

/**
 * Пользователь в API GoPing.
 */
public record UserDto(
        String id,
        String username,
        String nickname,
        String bio,
        String avatarUrl,
        Instant createdAt,
        Instant updatedAt,
        boolean online,
        Instant lastSeenAt
) {
}

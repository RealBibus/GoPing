package com.goping.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Минимальное представление чата.
 * Для личных чатов заголовок и аватар собеседника вычисляются из
 * chat_members, поэтому остаются актуальными после смены профиля.
 */
public record Chat(
        UUID id,
        String kind,
        String title,
        String username,
        String avatarPath,
        String avatarUrl,
        Instant createdAt,
        UUID peerUserId
) {
}

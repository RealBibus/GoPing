package com.goping.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Пользователь GoPing. Username уникален и хранится в нижнем регистре.
 * Nickname уникальность НЕ проверяет.
 *
 * <p>avatarPath — локально загруженная аватарка, avatarUrl — удалённая
 * (например, из GitHub). Если задан avatarPath, он имеет приоритет.</p>
 */
public record User(
        UUID id,
        String username,
        String nickname,
        String bio,
        String avatarPath,
        String avatarUrl,
        String githubId,
        Instant createdAt,
        Instant updatedAt
) {
}

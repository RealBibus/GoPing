package com.aerisflow.messenger.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Пользователь. Username уникален и хранится в нижнем регистре.
 * Nickname уникальность НЕ проверяет.
 */
public record User(
        UUID id,
        String username,
        String nickname,
        String email,
        String passwordHash,
        String bio,
        String avatarPath,
        Instant createdAt,
        Instant updatedAt
) {
}

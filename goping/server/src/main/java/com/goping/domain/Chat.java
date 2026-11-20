package com.goping.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Минимальное представление чата.
 * Сейчас чатов нет: GET /api/v1/chats отдаёт пустой список, клиент показывает заглушку.
 */
public record Chat(
        UUID id,
        String kind,
        String title,
        Instant createdAt
) {
}

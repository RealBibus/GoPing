package com.aerisflow.messenger.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Refresh-сессия. В базе хранится только SHA-256 от токена.
 */
public record Session(
        UUID id,
        UUID userId,
        String tokenHash,
        Instant expiresAt,
        Instant createdAt,
        Instant revokedAt,
        String userAgent,
        String ip
) {
}

package com.goping.config;

import org.springframework.boot.context.properties.ConfigurationProperties;

import java.time.Duration;
import java.util.List;

/**
 * Настройки GoPing из application.yml / переменных окружения.
 */
@ConfigurationProperties(prefix = "app")
public record AppProperties(
        String databasePath,
        String jwtSecret,
        String jwtIssuer,
        Duration accessTokenTtl,
        Duration refreshTokenTtl,
        String uploadDir,
        String publicBaseUrl,
        long maxAvatarBytes,
        int authRateLimit,
        int availabilityRateLimit,
        List<String> corsOrigins,
        boolean autoMigrate,
        String adminSecret,
        String messageKey,
        String githubClientId,
        String githubClientSecret,
        String githubCallbackUrl,
        String githubScope
) {

    public AppProperties {
        if (databasePath == null || databasePath.isBlank()) {
            databasePath = "./data/goping.db";
        }
        if (jwtSecret == null || jwtSecret.length() < 32) {
            throw new IllegalStateException("JWT_SECRET is required and must be at least 32 characters long");
        }
        if (jwtIssuer == null || jwtIssuer.isBlank()) {
            jwtIssuer = "goping";
        }
        if (accessTokenTtl == null || accessTokenTtl.isNegative() || accessTokenTtl.isZero()) {
            throw new IllegalStateException("ACCESS_TOKEN_TTL must be positive");
        }
        if (refreshTokenTtl == null || refreshTokenTtl.isNegative() || refreshTokenTtl.isZero()) {
            throw new IllegalStateException("REFRESH_TOKEN_TTL must be positive");
        }
        if (refreshTokenTtl.compareTo(accessTokenTtl) <= 0) {
            throw new IllegalStateException("REFRESH_TOKEN_TTL must be greater than ACCESS_TOKEN_TTL");
        }
        if (uploadDir == null || uploadDir.isBlank()) {
            uploadDir = "./data/uploads";
        }
        if (publicBaseUrl == null) {
            publicBaseUrl = "";
        }
        publicBaseUrl = publicBaseUrl.replaceAll("/+$", "");
        if (maxAvatarBytes <= 0) {
            maxAvatarBytes = 5L * 1024 * 1024;
        }
        if (authRateLimit <= 0) {
            authRateLimit = 30;
        }
        if (availabilityRateLimit <= 0) {
            availabilityRateLimit = 120;
        }
        if (corsOrigins == null || corsOrigins.isEmpty()) {
            corsOrigins = List.of("*");
        }
        adminSecret = adminSecret == null ? "" : adminSecret.trim();
        messageKey = messageKey == null ? "" : messageKey.trim();
        githubClientId = githubClientId == null ? "" : githubClientId.trim();
        githubClientSecret = githubClientSecret == null ? "" : githubClientSecret.trim();
        if (githubCallbackUrl == null || githubCallbackUrl.isBlank()) {
            githubCallbackUrl = (publicBaseUrl.isEmpty() ? "http://localhost:8080" : publicBaseUrl)
                    + "/api/v1/auth/github/callback";
        }
        githubCallbackUrl = githubCallbackUrl.replaceAll("/+$", "");
        if (githubScope == null || githubScope.isBlank()) {
            githubScope = "read:user";
        }
    }

    public boolean githubConfigured() {
        return !githubClientId.isBlank() && !githubClientSecret.isBlank();
    }

    public boolean adminEnabled() {
        return !adminSecret.isBlank();
    }
}

package com.aerisflow.messenger.config;

import org.springframework.boot.context.properties.ConfigurationProperties;

import java.time.Duration;
import java.util.List;

/**
 * Настройки приложения из application.yml / переменных окружения.
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
        boolean autoMigrate
) {

    public AppProperties {
        if (databasePath == null || databasePath.isBlank()) {
            databasePath = "./data/messenger.db";
        }
        if (jwtSecret == null || jwtSecret.length() < 32) {
            throw new IllegalStateException("JWT_SECRET is required and must be at least 32 characters long");
        }
        if (jwtIssuer == null || jwtIssuer.isBlank()) {
            jwtIssuer = "aerisflow-messenger";
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
    }
}

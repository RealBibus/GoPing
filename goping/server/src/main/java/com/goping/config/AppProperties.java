package com.goping.config;

import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.boot.context.properties.bind.ConstructorBinding;

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
        Boolean allowDeleteOthersMessages,
        String adminSecret,
        String messageKey,
        String githubClientId,
        String githubClientSecret,
        String githubCallbackUrl,
        String githubScope,
        int maxTextLength,
        int maxVoiceSeconds,
        int maxCallMinutes,
        long maxFileBytes,
        int logLines
) {

    /** Backward-compatible constructor for callers that use the 1.0.1 property set. */
    public AppProperties(String databasePath, String jwtSecret, String jwtIssuer,
                         Duration accessTokenTtl, Duration refreshTokenTtl, String uploadDir,
                         String publicBaseUrl, long maxAvatarBytes, int authRateLimit,
                         int availabilityRateLimit, List<String> corsOrigins, boolean autoMigrate,
                         Boolean allowDeleteOthersMessages, String adminSecret, String messageKey,
                         String githubClientId, String githubClientSecret, String githubCallbackUrl,
                         String githubScope) {
        this(databasePath, jwtSecret, jwtIssuer, accessTokenTtl, refreshTokenTtl, uploadDir,
                publicBaseUrl, maxAvatarBytes, authRateLimit, availabilityRateLimit, corsOrigins,
                autoMigrate, allowDeleteOthersMessages, adminSecret, messageKey, githubClientId,
                githubClientSecret, githubCallbackUrl, githubScope,
                4000, 300, 0, 52428800L, 400);
    }

    @ConstructorBinding
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
        if (maxTextLength <= 0) {
            throw new IllegalStateException("MAX_TEXT_LENGTH must be positive");
        }
        if (maxVoiceSeconds <= 0) {
            throw new IllegalStateException("MAX_VOICE_SECONDS must be positive");
        }
        if (maxCallMinutes < 0) {
            throw new IllegalStateException("MAX_CALL_MINUTES must not be negative");
        }
        if (maxFileBytes <= 0) {
            throw new IllegalStateException("MAX_FILE_BYTES must be positive");
        }
        if (logLines <= 0) {
            throw new IllegalStateException("LOG_LINES must be positive");
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
        if (allowDeleteOthersMessages == null) {
            allowDeleteOthersMessages = true;
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

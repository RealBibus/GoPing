package com.goping.service;

import com.goping.config.AppProperties;
import com.goping.domain.Session;
import com.goping.domain.User;
import com.goping.error.ApiException;
import com.goping.github.GitHubClient;
import com.goping.repository.ServerTokenRepository.StoredServerToken;
import com.goping.repository.SessionRepository;
import com.goping.repository.UserRepository;
import com.goping.security.TokenService;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Duration;
import java.time.Instant;
import java.util.Base64;
import java.util.HexFormat;
import java.util.Locale;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Вход по серверному токену, вход через GitHub, refresh (с ротацией) и выход.
 * Паролей и регистрации больше нет.
 */
@Service
public class AuthService {

    private final UserRepository users;
    private final SessionRepository sessions;
    private final TokenService tokens;
    private final ServerTokenService serverTokens;
    private final Duration refreshTtl;
    private final SecureRandom random = new SecureRandom();
    private final ConcurrentHashMap<UUID, Object> activationLocks = new ConcurrentHashMap<>();

    public record SessionMeta(String userAgent, String ip) {
    }

    public record AuthResult(User user, String accessToken, String refreshToken, Instant expiresAt) {
    }

    private record RefreshToken(String raw, String hash) {
    }

    public AuthService(UserRepository users,
                       SessionRepository sessions,
                       TokenService tokens,
                       ServerTokenService serverTokens,
                       AppProperties properties) {
        this.users = users;
        this.sessions = sessions;
        this.tokens = tokens;
        this.serverTokens = serverTokens;
        this.refreshTtl = properties.refreshTokenTtl();
    }

    /**
     * Первый вызов активирует токен и создаёт пользователя. Все последующие
     * вызовы с тем же токеном входят в этого же пользователя.
     */
    @Transactional
    public AuthResult loginWithServerToken(String rawToken, SessionMeta meta) {
        String raw = ServerTokenService.normalize(rawToken);
        if (!ServerTokenService.validFormat(raw)) {
            throw ApiException.invalidServerToken();
        }

        StoredServerToken stored = serverTokens.find(raw)
                .orElseThrow(ApiException::invalidServerToken);
        if (stored.revokedAt() != null) {
            throw ApiException.invalidServerToken();
        }

        if (stored.userId() != null) {
            User user = users.findById(stored.userId())
                    .orElseThrow(ApiException::invalidServerToken);
            serverTokens.touch(stored.id());
            return issue(user, meta);
        }

        synchronized (activationLocks.computeIfAbsent(stored.id(), ignored -> new Object())) {
            StoredServerToken fresh = serverTokens.find(raw)
                    .orElseThrow(ApiException::invalidServerToken);
            if (fresh.revokedAt() != null) {
                throw ApiException.invalidServerToken();
            }
            if (fresh.userId() != null) {
                User user = users.findById(fresh.userId())
                        .orElseThrow(ApiException::invalidServerToken);
                serverTokens.touch(fresh.id());
                return issue(user, meta);
            }

            User created = newTokenUser();
            users.create(created);
            if (!serverTokens.bind(fresh.id(), created.id())) {
                // Токен успели активировать в другом процессе: не оставляем
                // осиротевшего пользователя и входим в настоящего владельца.
                users.delete(created.id());
                StoredServerToken bound = serverTokens.find(raw)
                        .orElseThrow(ApiException::invalidServerToken);
                if (bound.userId() == null) {
                    throw ApiException.invalidServerToken();
                }
                return issue(users.findById(bound.userId())
                        .orElseThrow(ApiException::invalidServerToken), meta);
            }
            serverTokens.touch(fresh.id());
            return issue(created, meta);
        }
    }

    /** Вход через GitHub: первый раз создаёт профиль, дальше узнаёт по github_id. */
    @Transactional
    public AuthResult loginWithGithub(GitHubClient.Profile profile, SessionMeta meta) {
        String githubId = Long.toString(profile.id());
        Optional<User> existing = users.findByGithubId(githubId);
        if (existing.isPresent()) {
            return issue(existing.get(), meta);
        }

        User created = newGithubUser(profile);
        try {
            users.create(created);
        } catch (ApiException e) {
            if ("GITHUB_ALREADY_LINKED".equals(e.code())) {
                return issue(users.findByGithubId(githubId)
                        .orElseThrow(ApiException::githubAuthFailed), meta);
            }
            throw e;
        }
        return issue(created, meta);
    }

    /**
     * Меняет refresh-токен на новую пару. Повторное использование отозванного
     * токена отзывает все сессии пользователя.
     *
     * <p>noRollbackFor: отзыв всех сессий при краже токена должен сохраниться,
     * хотя запрос завершается ошибкой.</p>
     */
    @Transactional(noRollbackFor = ApiException.class)
    public AuthResult refresh(String rawToken, SessionMeta meta) {
        String raw = rawToken == null ? "" : rawToken.trim();
        if (raw.isEmpty()) {
            throw ApiException.invalidRefreshToken();
        }

        Session session = sessions.findByTokenHash(hashToken(raw))
                .orElseThrow(ApiException::invalidRefreshToken);

        if (session.revokedAt() != null) {
            sessions.revokeAllForUser(session.userId());
            throw ApiException.invalidRefreshToken();
        }

        if (Instant.now().isAfter(session.expiresAt())) {
            sessions.revoke(session.id());
            throw ApiException.invalidRefreshToken();
        }

        User user = users.findById(session.userId()).orElseGet(() -> {
            sessions.revokeAllForUser(session.userId());
            throw ApiException.invalidRefreshToken();
        });

        RefreshToken refresh = newRefreshToken();
        Session newSession = createSession(user.id(), refresh.hash(), meta);

        if (!sessions.rotate(session.id(), newSession.id())) {
            sessions.delete(newSession.id());
            sessions.revokeAllForUser(user.id());
            throw ApiException.invalidRefreshToken();
        }

        TokenService.Issued access = tokens.issue(user.id());
        return new AuthResult(user, access.token(), refresh.raw(), access.expiresAt());
    }

    public void logout(String rawToken) {
        String raw = rawToken == null ? "" : rawToken.trim();
        if (raw.isEmpty()) {
            return;
        }
        sessions.findByTokenHash(hashToken(raw)).ifPresent(session -> {
            if (session.revokedAt() == null) {
                sessions.revoke(session.id());
            }
        });
    }

    private User newTokenUser() {
        String username = uniqueUsername("ping_" + randomLower(8));
        String nickname = "Ping " + randomUpper(4);
        Instant now = Instant.now();
        return new User(UUID.randomUUID(), username, nickname, "", null, null, null, now, now);
    }

    private User newGithubUser(GitHubClient.Profile profile) {
        String username = uniqueUsername(sanitizeGithubLogin(profile.login()));
        String nickname = githubNickname(profile);
        String avatarUrl = profile.avatarUrl() != null
                && (profile.avatarUrl().startsWith("https://") || profile.avatarUrl().startsWith("http://"))
                ? profile.avatarUrl()
                : null;
        Instant now = Instant.now();
        return new User(UUID.randomUUID(), username, nickname, "", null, avatarUrl,
                Long.toString(profile.id()), now, now);
    }

    private String uniqueUsername(String base) {
        String candidate = base;
        for (int attempt = 0; attempt < 40; attempt++) {
            if (!Validation.username(candidate).hasError() && !users.usernameExists(candidate)) {
                return candidate;
            }
            String suffix = "_" + randomLower(4);
            int keep = Math.max(1, 32 - suffix.length());
            candidate = (candidate.length() > keep ? candidate.substring(0, keep) : candidate) + suffix;
        }
        throw new ApiException(org.springframework.http.HttpStatus.INTERNAL_SERVER_ERROR,
                "INTERNAL", "Не удалось подобрать имя пользователя");
    }

    private static String sanitizeGithubLogin(String login) {
        String lower = login == null ? "" : login.toLowerCase(Locale.ROOT);
        StringBuilder builder = new StringBuilder();
        for (char c : lower.toCharArray()) {
            builder.append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' ? c : '_');
        }
        String value = builder.toString();
        if (value.isEmpty() || value.charAt(0) < 'a' || value.charAt(0) > 'z') {
            value = "gh_" + value;
        }
        if (value.length() > 32) {
            value = value.substring(0, 32);
        }
        return value;
    }

    private static String githubNickname(GitHubClient.Profile profile) {
        String raw = profile.name() == null || profile.name().isBlank()
                ? profile.login()
                : profile.name();
        String clean = raw == null ? "" : raw.trim();
        if (clean.codePointCount(0, clean.length()) > 50) {
            int end = clean.offsetByCodePoints(0, 50);
            clean = clean.substring(0, end);
        }
        Validation.Checked checked = Validation.nickname(clean);
        return checked.hasError() ? "GitHub user" : checked.value();
    }

    private AuthResult issue(User user, SessionMeta meta) {
        RefreshToken refresh = newRefreshToken();
        createSession(user.id(), refresh.hash(), meta);
        TokenService.Issued access = tokens.issue(user.id());
        return new AuthResult(user, access.token(), refresh.raw(), access.expiresAt());
    }

    private Session createSession(UUID userId, String tokenHash, SessionMeta meta) {
        Instant now = Instant.now();
        Session session = new Session(
                UUID.randomUUID(),
                userId,
                tokenHash,
                now.plus(refreshTtl),
                now,
                null,
                truncate(meta.userAgent(), 512),
                truncate(meta.ip(), 64)
        );
        sessions.create(session);
        return session;
    }

    private RefreshToken newRefreshToken() {
        byte[] bytes = new byte[32];
        random.nextBytes(bytes);
        String raw = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes);
        return new RefreshToken(raw, hashToken(raw));
    }

    private String randomLower(int length) {
        return randomString(length, "abcdefghijklmnopqrstuvwxyz0123456789");
    }

    private String randomUpper(int length) {
        return randomString(length, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
    }

    private String randomString(int length, String alphabet) {
        StringBuilder builder = new StringBuilder(length);
        for (int i = 0; i < length; i++) {
            builder.append(alphabet.charAt(random.nextInt(alphabet.length())));
        }
        return builder.toString();
    }

    static String hashToken(String raw) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            return HexFormat.of().formatHex(digest.digest(raw.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is not available", e);
        }
    }

    private static String truncate(String value, int max) {
        if (value == null) {
            return "";
        }
        if (value.length() <= max) {
            return value;
        }
        int end = max;
        if (Character.isHighSurrogate(value.charAt(end - 1))) {
            end--;
        }
        return value.substring(0, end);
    }
}

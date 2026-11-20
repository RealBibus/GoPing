package com.aerisflow.messenger.service;

import com.aerisflow.messenger.config.AppProperties;
import com.aerisflow.messenger.domain.Session;
import com.aerisflow.messenger.domain.User;
import com.aerisflow.messenger.error.ApiException;
import com.aerisflow.messenger.error.ValidationException;
import com.aerisflow.messenger.repository.SessionRepository;
import com.aerisflow.messenger.repository.UserRepository;
import com.aerisflow.messenger.security.TokenService;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Duration;
import java.time.Instant;
import java.util.Base64;
import java.util.HashMap;
import java.util.HexFormat;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

/**
 * Регистрация, вход, refresh (с ротацией) и выход.
 */
@Service
public class AuthService {

    private final UserRepository users;
    private final SessionRepository sessions;
    private final TokenService tokens;
    private final PasswordEncoder passwordEncoder;
    private final Duration refreshTtl;
    private final SecureRandom random = new SecureRandom();
    private final String dummyHash;

    public record RegisterInput(String email, String username, String password, String nickname) {
    }

    public record LoginInput(String login, String password) {
    }

    public record SessionMeta(String userAgent, String ip) {
    }

    public record AuthResult(User user, String accessToken, String refreshToken, Instant expiresAt) {
    }

    private record RefreshToken(String raw, String hash) {
    }

    public AuthService(UserRepository users,
                       SessionRepository sessions,
                       TokenService tokens,
                       PasswordEncoder passwordEncoder,
                       AppProperties properties) {
        this.users = users;
        this.sessions = sessions;
        this.tokens = tokens;
        this.passwordEncoder = passwordEncoder;
        this.refreshTtl = properties.refreshTokenTtl();
        this.dummyHash = passwordEncoder.encode("timing-equalization-dummy");
    }

    public AuthResult register(RegisterInput input, SessionMeta meta) {
        Map<String, String> errors = new HashMap<>();
        String email = take(errors, "email", Validation.email(input.email()));
        String username = take(errors, "username", Validation.username(input.username()));
        String password = take(errors, "password", Validation.password(input.password()));

        String rawNickname = input.nickname() == null ? "" : input.nickname().trim();
        String nickname;
        if (rawNickname.isEmpty()) {
            nickname = username;
        } else {
            nickname = take(errors, "nickname", Validation.nickname(rawNickname));
        }

        if (!errors.isEmpty()) {
            throw new ValidationException(errors);
        }

        Instant now = Instant.now();
        User user = new User(
                UUID.randomUUID(),
                username,
                nickname,
                email,
                passwordEncoder.encode(password),
                "",
                null,
                now,
                now
        );

        users.create(user);
        return issue(user, meta);
    }

    public AuthResult login(LoginInput input, SessionMeta meta) {
        String login = input.login() == null ? "" : input.login().trim().toLowerCase(Locale.ROOT);

        Map<String, String> errors = new HashMap<>();
        if (login.isEmpty()) {
            errors.put("login", "укажите email или имя пользователя");
        }
        if (input.password() == null || input.password().isEmpty()) {
            errors.put("password", "укажите пароль");
        }
        if (!errors.isEmpty()) {
            throw new ValidationException(errors);
        }

        Optional<User> found = users.findByLogin(login);
        if (found.isEmpty()) {
            // Выравниваем время ответа при несуществующем логине.
            passwordEncoder.matches(input.password(), dummyHash);
            throw ApiException.invalidCredentials();
        }

        User user = found.get();
        if (!passwordEncoder.matches(input.password(), user.passwordHash())) {
            throw ApiException.invalidCredentials();
        }

        return issue(user, meta);
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

    static String hashToken(String raw) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            return HexFormat.of().formatHex(digest.digest(raw.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is not available", e);
        }
    }

    private static String take(Map<String, String> errors, String field, Validation.Checked checked) {
        if (checked.hasError()) {
            errors.put(field, checked.error());
        }
        return checked.value();
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

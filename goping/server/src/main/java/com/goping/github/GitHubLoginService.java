package com.goping.github;

import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import com.goping.service.AuthService;
import org.springframework.stereotype.Service;
import org.springframework.web.util.UriComponentsBuilder;

import java.security.SecureRandom;
import java.time.Duration;
import java.time.Instant;
import java.util.Base64;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Браузерный GitHub OAuth: приложение открывает страницу GitHub,
 * а клиент опрашивает сервер по state и получает сессию, когда
 * пользователь разрешил доступ.
 */
@Service
public class GitHubLoginService {

    private static final Duration STATE_TTL = Duration.ofMinutes(10);

    private final AppProperties properties;
    private final GitHubClient github;
    private final AuthService auth;
    private final SecureRandom random = new SecureRandom();
    private final Map<String, Pending> pending = new ConcurrentHashMap<>();

    public record Start(String authorizeUrl, String state, Instant expiresAt) {
    }

    public record Poll(boolean ready, AuthService.AuthResult result) {
    }

    public GitHubLoginService(AppProperties properties, GitHubClient github, AuthService auth) {
        this.properties = properties;
        this.github = github;
        this.auth = auth;
    }

    public Start start() {
        if (!properties.githubConfigured()) {
            throw ApiException.githubNotConfigured();
        }
        cleanup();
        String state = randomState();
        Instant expiresAt = Instant.now().plus(STATE_TTL);
        pending.put(state, new Pending(expiresAt));

        String url = UriComponentsBuilder.fromHttpUrl("https://github.com/login/oauth/authorize")
                .queryParam("client_id", properties.githubClientId())
                .queryParam("redirect_uri", properties.githubCallbackUrl())
                .queryParam("scope", properties.githubScope())
                .queryParam("state", state)
                .queryParam("allow_signup", "true")
                .build()
                .toUriString();
        return new Start(url, state, expiresAt);
    }

    /** Вызывается из браузерного callback. Токены здесь не выдаются. */
    public void callback(String code, String state, String githubError) {
        Pending flow = state == null ? null : pending.get(state);
        if (flow == null || flow.isExpired()) {
            if (state != null) {
                pending.remove(state);
            }
            throw ApiException.githubStateExpired();
        }
        if (githubError != null && !githubError.isBlank()) {
            flow.fail("Доступ к GitHub отменён");
            throw ApiException.githubAuthFailed("Доступ к GitHub отменён");
        }
        if (flow.profile != null) {
            return;
        }
        try {
            flow.profile = github.authenticate(code);
        } catch (ApiException e) {
            flow.fail(e.getMessage());
            throw e;
        } catch (RuntimeException e) {
            flow.fail("Не удалось получить профиль GitHub");
            throw ApiException.githubAuthFailed("Не удалось получить профиль GitHub");
        }
    }

    public Poll poll(String state, AuthService.SessionMeta meta) {
        Pending flow = state == null ? null : pending.get(state);
        if (flow == null) {
            throw ApiException.githubStateExpired();
        }
        if (flow.isExpired()) {
            pending.remove(state);
            throw ApiException.githubStateExpired();
        }
        if (flow.error != null) {
            pending.remove(state);
            throw ApiException.githubAuthFailed(flow.error);
        }
        if (flow.profile == null) {
            return new Poll(false, null);
        }
        pending.remove(state);
        return new Poll(true, auth.loginWithGithub(flow.profile, meta));
    }

    public boolean configured() {
        return properties.githubConfigured();
    }

    private String randomState() {
        byte[] bytes = new byte[32];
        random.nextBytes(bytes);
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes);
    }

    private void cleanup() {
        Instant now = Instant.now();
        pending.entrySet().removeIf(entry -> entry.getValue().expiresAt.isBefore(now));
    }

    private static final class Pending {
        private final Instant expiresAt;
        private volatile GitHubClient.Profile profile;
        private volatile String error;

        private Pending(Instant expiresAt) {
            this.expiresAt = expiresAt;
        }

        private boolean isExpired() {
            return Instant.now().isAfter(expiresAt);
        }

        private void fail(String message) {
            this.error = message;
        }
    }
}

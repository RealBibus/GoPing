package com.goping.service;

import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import com.goping.repository.ServerTokenRepository;
import com.goping.repository.ServerTokenRepository.StoredServerToken;
import org.springframework.stereotype.Service;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Instant;
import java.util.HexFormat;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.regex.Pattern;

/**
 * Одноразовые серверные токены вида {@code GOPING-XXXXXXXXXXXX}.
 *
 * <p>Токен выдаёт администратор. При первой активации он привязывается
 * к новому пользователю и остаётся его постоянным ключом входа. Повторная
 * активация того же токена уже не создаёт нового пользователя — это вход
 * в того же самого пользователя.</p>
 */
@Service
public class ServerTokenService {

    public static final String PREFIX = "GOPING-";
    public static final int RANDOM_LENGTH = 12;

    private static final Pattern TOKEN_PATTERN = Pattern.compile("^GOPING-[A-Za-z0-9_-]{12}$");
    private static final char[] ALPHABET =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".toCharArray();

    private final ServerTokenRepository repository;
    private final SecureRandom random = new SecureRandom();

    public record IssuedToken(String value, StoredServerToken stored) {
    }

    public ServerTokenService(ServerTokenRepository repository, AppProperties properties) {
        this.repository = repository;
    }

    public IssuedToken issue(String label) {
        String value;
        String hash;
        do {
            value = PREFIX + randomPart(RANDOM_LENGTH);
            hash = hash(value);
        } while (repository.findByHash(hash).isPresent());

        UUID id = UUID.randomUUID();
        Instant now = Instant.now();
        String cleanLabel = label == null ? "" : label.trim();
        if (cleanLabel.length() > 80) {
            cleanLabel = cleanLabel.substring(0, 80);
        }
        repository.create(id, hash, PREFIX + value.substring(PREFIX.length(), PREFIX.length() + 4),
                cleanLabel, now);
        StoredServerToken stored = repository.findByHash(hash).orElseThrow();
        return new IssuedToken(value, stored);
    }

    public Optional<StoredServerToken> find(String raw) {
        String token = normalize(raw);
        if (!validFormat(token)) {
            return Optional.empty();
        }
        return repository.findByHash(hash(token));
    }

    public List<StoredServerToken> list() {
        return repository.list();
    }

    public void revoke(UUID id) {
        if (id == null) {
            throw ApiException.notFound();
        }
        repository.revoke(id, Instant.now());
    }

    public void touch(UUID id) {
        repository.touch(id, Instant.now());
    }

    public boolean bind(UUID id, UUID userId) {
        return repository.bind(id, userId, Instant.now());
    }

    public static String normalize(String raw) {
        return raw == null ? "" : raw.trim();
    }

    public static boolean validFormat(String raw) {
        return raw != null && TOKEN_PATTERN.matcher(raw).matches();
    }

    public static String hash(String raw) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            return HexFormat.of().formatHex(digest.digest(raw.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is not available", e);
        }
    }

    private String randomPart(int length) {
        StringBuilder builder = new StringBuilder(length);
        for (int i = 0; i < length; i++) {
            builder.append(ALPHABET[random.nextInt(ALPHABET.length)]);
        }
        return builder.toString();
    }
}

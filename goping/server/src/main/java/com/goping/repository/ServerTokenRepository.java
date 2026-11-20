package com.goping.repository;

import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

@Repository
public class ServerTokenRepository {

    public record StoredServerToken(
            UUID id,
            String tokenHash,
            String prefix,
            String label,
            UUID userId,
            Instant createdAt,
            Instant activatedAt,
            Instant lastUsedAt,
            Instant revokedAt
    ) {
    }

    private static final RowMapper<StoredServerToken> MAPPER = (rs, rowNum) -> new StoredServerToken(
            UUID.fromString(rs.getString("id")),
            rs.getString("token_hash"),
            rs.getString("token_prefix"),
            rs.getString("label"),
            uuidOrNull(rs.getString("user_id")),
            instant(rs, "created_at"),
            instant(rs, "activated_at"),
            instant(rs, "last_used_at"),
            instant(rs, "revoked_at")
    );

    private final JdbcTemplate jdbc;

    public ServerTokenRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public void create(UUID id, String tokenHash, String prefix, String label, Instant createdAt) {
        jdbc.update("""
                        INSERT INTO server_tokens
                            (id, token_hash, token_prefix, label, created_at)
                        VALUES (?, ?, ?, ?, ?)
                        """,
                id.toString(), tokenHash, prefix, label, createdAt.toEpochMilli());
    }

    public Optional<StoredServerToken> findByHash(String tokenHash) {
        List<StoredServerToken> tokens = jdbc.query("""
                        SELECT id, token_hash, token_prefix, label, user_id,
                               created_at, activated_at, last_used_at, revoked_at
                        FROM server_tokens
                        WHERE token_hash = ?
                        """,
                MAPPER, tokenHash);
        return tokens.stream().findFirst();
    }

    public List<StoredServerToken> list() {
        return jdbc.query("""
                        SELECT id, token_hash, token_prefix, label, user_id,
                               created_at, activated_at, last_used_at, revoked_at
                        FROM server_tokens
                        ORDER BY created_at DESC, id DESC
                        """,
                MAPPER);
    }

    /** Атомарно привязывает ещё не активированный токен к пользователю. */
    public boolean bind(UUID id, UUID userId, Instant now) {
        int updated = jdbc.update("""
                        UPDATE server_tokens
                        SET user_id = ?, activated_at = ?, last_used_at = ?
                        WHERE id = ? AND user_id IS NULL AND revoked_at IS NULL
                        """,
                userId.toString(), now.toEpochMilli(), now.toEpochMilli(), id.toString());
        return updated == 1;
    }

    public void touch(UUID id, Instant now) {
        jdbc.update("UPDATE server_tokens SET last_used_at = ? WHERE id = ?",
                now.toEpochMilli(), id.toString());
    }

    public void revoke(UUID id, Instant now) {
        jdbc.update("UPDATE server_tokens SET revoked_at = COALESCE(revoked_at, ?) WHERE id = ?",
                now.toEpochMilli(), id.toString());
    }

    private static UUID uuidOrNull(String value) {
        return value == null ? null : UUID.fromString(value);
    }

    private static Instant instant(ResultSet rs, String column) throws SQLException {
        long value = rs.getLong(column);
        return rs.wasNull() ? null : Instant.ofEpochMilli(value);
    }
}

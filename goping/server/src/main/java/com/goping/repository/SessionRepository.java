package com.goping.repository;

import com.goping.domain.Session;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

@Repository
public class SessionRepository {

    private static final RowMapper<Session> SESSION_MAPPER = (rs, rowNum) -> {
        long revokedAt = rs.getLong("revoked_at");
        boolean revokedNull = rs.wasNull();
        return new Session(
                UUID.fromString(rs.getString("id")),
                UUID.fromString(rs.getString("user_id")),
                rs.getString("token_hash"),
                Instant.ofEpochMilli(rs.getLong("expires_at")),
                Instant.ofEpochMilli(rs.getLong("created_at")),
                revokedNull ? null : Instant.ofEpochMilli(revokedAt),
                rs.getString("user_agent"),
                rs.getString("ip")
        );
    };

    private final JdbcTemplate jdbc;

    public SessionRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public void create(Session session) {
        jdbc.update("""
                        INSERT INTO refresh_tokens
                            (id, user_id, token_hash, expires_at, created_at, user_agent, ip)
                        VALUES (?, ?, ?, ?, ?, ?, ?)
                        """,
                session.id().toString(),
                session.userId().toString(),
                session.tokenHash(),
                session.expiresAt().toEpochMilli(),
                session.createdAt().toEpochMilli(),
                session.userAgent(),
                session.ip());
    }

    public Optional<Session> findByTokenHash(String tokenHash) {
        List<Session> sessions = jdbc.query("""
                        SELECT id, user_id, token_hash, expires_at, created_at, revoked_at, user_agent, ip
                        FROM refresh_tokens
                        WHERE token_hash = ?
                        """,
                SESSION_MAPPER, tokenHash);
        return sessions.stream().findFirst();
    }

    public void revoke(UUID id) {
        jdbc.update("UPDATE refresh_tokens SET revoked_at = COALESCE(revoked_at, ?) WHERE id = ?",
                System.currentTimeMillis(), id.toString());
    }

    /**
     * Атомарно отзывает старую сессию и записывает замену.
     * false — сессию уже отозвали параллельно.
     */
    public boolean rotate(UUID id, UUID replacementId) {
        int updated = jdbc.update("""
                        UPDATE refresh_tokens
                        SET revoked_at = ?, replaced_by_id = ?
                        WHERE id = ? AND revoked_at IS NULL
                        """,
                System.currentTimeMillis(), replacementId.toString(), id.toString());
        return updated == 1;
    }

    public void revokeAllForUser(UUID userId) {
        jdbc.update("UPDATE refresh_tokens SET revoked_at = ? WHERE user_id = ? AND revoked_at IS NULL",
                System.currentTimeMillis(), userId.toString());
    }

    public void delete(UUID id) {
        jdbc.update("DELETE FROM refresh_tokens WHERE id = ?", id.toString());
    }
}

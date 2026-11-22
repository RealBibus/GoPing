package com.goping.repository;

import com.goping.domain.User;
import com.goping.error.ApiException;
import org.springframework.dao.DataIntegrityViolationException;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

@Repository
public class UserRepository {

    private static final RowMapper<User> USER_MAPPER = (rs, rowNum) -> new User(
            UUID.fromString(rs.getString("id")),
            rs.getString("username"),
            rs.getString("nickname"),
            rs.getString("bio"),
            rs.getString("avatar_path"),
            rs.getString("avatar_url"),
            rs.getString("github_id"),
            Instant.ofEpochMilli(rs.getLong("created_at")),
            Instant.ofEpochMilli(rs.getLong("updated_at"))
    );

    private static final String COLUMNS =
            "id, username, nickname, bio, avatar_path, avatar_url, github_id, created_at, updated_at";

    private final JdbcTemplate jdbc;

    public UserRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    /** Частичное обновление профиля; null означает «не менять». */
    public record ProfilePatch(String nickname, String username, String bio) {
    }

    public void create(User user) {
        try {
            // email/password_hash — legacy-колонки из первой версии схемы.
            // Новый вход их не использует, но таблица остаётся совместимой.
            jdbc.update("""
                            INSERT INTO users
                                (id, username, nickname, email, password_hash, bio,
                                 avatar_path, avatar_url, github_id, created_at, updated_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                            """,
                    user.id().toString(),
                    user.username(),
                    user.nickname(),
                    user.id() + "@goping.local",
                    "",
                    user.bio(),
                    user.avatarPath(),
                    user.avatarUrl(),
                    user.githubId(),
                    user.createdAt().toEpochMilli(),
                    user.updatedAt().toEpochMilli());
        } catch (DataIntegrityViolationException e) {
            throw mapUniqueViolation(e);
        }
    }

    public Optional<User> findById(UUID id) {
        List<User> users = jdbc.query(
                "SELECT " + COLUMNS + " FROM users WHERE id = ?",
                USER_MAPPER, id.toString());
        return users.stream().findFirst();
    }

    public Optional<User> findByUsername(String username) {
        if (username == null || username.isBlank()) {
            return Optional.empty();
        }
        List<User> users = jdbc.query(
                "SELECT " + COLUMNS + " FROM users WHERE username = ? LIMIT 1",
                USER_MAPPER, username);
        return users.stream().findFirst();
    }

    /**
     * Поиск по подстроке username. Символы !, % и _ экранируются,
     * чтобы ввод не менял смысл LIKE-шаблона.
     */
    public List<User> searchByUsername(String query, UUID excludeId, int limit) {
        if (query == null || query.isBlank()) {
            return List.of();
        }
        StringBuilder escaped = new StringBuilder(query.length() + 2);
        for (int i = 0; i < query.length(); i++) {
            char c = query.charAt(i);
            if (c == '!' || c == '%' || c == '_') {
                escaped.append('!');
            }
            escaped.append(c);
        }
        String pattern = "%" + escaped + "%";
        return jdbc.query(
                "SELECT " + COLUMNS + " FROM users WHERE username LIKE ? ESCAPE '!' "
                        + "AND id <> ? ORDER BY username LIMIT ?",
                USER_MAPPER, pattern, excludeId.toString(), limit);
    }

    public Optional<User> findByGithubId(String githubId) {
        if (githubId == null || githubId.isBlank()) {
            return Optional.empty();
        }
        List<User> users = jdbc.query(
                "SELECT " + COLUMNS + " FROM users WHERE github_id = ? LIMIT 1",
                USER_MAPPER, githubId);
        return users.stream().findFirst();
    }

    public boolean usernameExists(String username) {
        Integer count = jdbc.queryForObject(
                "SELECT COUNT(1) FROM users WHERE username = ?", Integer.class, username);
        return count != null && count > 0;
    }

    public void delete(UUID id) {
        jdbc.update("DELETE FROM users WHERE id = ?", id.toString());
    }

    public Optional<User> updateProfile(UUID id, ProfilePatch patch, Instant updatedAt) {
        jdbc.update("""
                        UPDATE users SET
                            nickname   = COALESCE(?, nickname),
                            username   = COALESCE(?, username),
                            bio        = COALESCE(?, bio),
                            updated_at = ?
                        WHERE id = ?
                        """,
                patch.nickname(), patch.username(), patch.bio(), updatedAt.toEpochMilli(), id.toString());
        return findById(id);
    }

    public Optional<User> setAvatar(UUID id, String avatarPath, Instant updatedAt) {
        jdbc.update("UPDATE users SET avatar_path = ?, avatar_url = NULL, updated_at = ? WHERE id = ?",
                avatarPath, updatedAt.toEpochMilli(), id.toString());
        return findById(id);
    }

    public Optional<User> clearAvatar(UUID id, Instant updatedAt) {
        jdbc.update("UPDATE users SET avatar_path = NULL, avatar_url = NULL, updated_at = ? WHERE id = ?",
                updatedAt.toEpochMilli(), id.toString());
        return findById(id);
    }

    private RuntimeException mapUniqueViolation(DataIntegrityViolationException e) {
        String message = e.getMostSpecificCause().getMessage();
        if (message != null) {
            if (message.contains("users.username")) {
                return ApiException.usernameTaken();
            }
            if (message.contains("users.github_id")) {
                return ApiException.githubAlreadyLinked();
            }
        }
        return e;
    }
}

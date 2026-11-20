package com.aerisflow.messenger.repository;

import com.aerisflow.messenger.domain.User;
import com.aerisflow.messenger.error.ApiException;
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
            rs.getString("email"),
            rs.getString("password_hash"),
            rs.getString("bio"),
            rs.getString("avatar_path"),
            Instant.ofEpochMilli(rs.getLong("created_at")),
            Instant.ofEpochMilli(rs.getLong("updated_at"))
    );

    private static final String COLUMNS =
            "id, username, nickname, email, password_hash, bio, avatar_path, created_at, updated_at";

    private final JdbcTemplate jdbc;

    public UserRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    /** Частичное обновление профиля; null означает «не менять». */
    public record ProfilePatch(String nickname, String username, String bio) {
    }

    public void create(User user) {
        try {
            jdbc.update("""
                            INSERT INTO users
                                (id, username, nickname, email, password_hash, bio, avatar_path, created_at, updated_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                            """,
                    user.id().toString(),
                    user.username(),
                    user.nickname(),
                    user.email(),
                    user.passwordHash(),
                    user.bio(),
                    user.avatarPath(),
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

    public Optional<User> findByLogin(String login) {
        List<User> users = jdbc.query(
                "SELECT " + COLUMNS + " FROM users WHERE username = ? OR email = ? LIMIT 1",
                USER_MAPPER, login, login);
        return users.stream().findFirst();
    }

    public boolean usernameExists(String username) {
        Integer count = jdbc.queryForObject(
                "SELECT COUNT(1) FROM users WHERE username = ?", Integer.class, username);
        return count != null && count > 0;
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
        jdbc.update("UPDATE users SET avatar_path = ?, updated_at = ? WHERE id = ?",
                avatarPath, updatedAt.toEpochMilli(), id.toString());
        return findById(id);
    }

    public Optional<User> clearAvatar(UUID id, Instant updatedAt) {
        jdbc.update("UPDATE users SET avatar_path = NULL, updated_at = ? WHERE id = ?",
                updatedAt.toEpochMilli(), id.toString());
        return findById(id);
    }

    private RuntimeException mapUniqueViolation(DataIntegrityViolationException e) {
        String message = e.getMostSpecificCause().getMessage();
        if (message != null) {
            if (message.contains("users.username")) {
                return ApiException.usernameTaken();
            }
            if (message.contains("users.email")) {
                return ApiException.emailTaken();
            }
        }
        return e;
    }
}

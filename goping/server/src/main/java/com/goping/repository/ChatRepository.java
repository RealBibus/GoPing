package com.goping.repository;

import com.goping.domain.Chat;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

/**
 * Чаты пользователя и их участники.
 *
 * <p>Для личных чатов заголовок не хранится: он вычисляется как никнейм
 * собеседника, поэтому остаётся актуальным после смены профиля.</p>
 */
@Repository
public class ChatRepository {

    private static final RowMapper<Chat> CHAT_MAPPER = (rs, rowNum) -> {
        String peerId = rs.getString("peer_user_id");
        return new Chat(
                UUID.fromString(rs.getString("id")),
                rs.getString("kind"),
                rs.getString("title"),
                rs.getString("username"),
                rs.getString("avatar_path"),
                rs.getString("avatar_url"),
                Instant.ofEpochMilli(rs.getLong("created_at")),
                peerId == null || peerId.isBlank() ? null : UUID.fromString(peerId)
        );
    };

    /**
     * Колонки чата с вычисляемыми полями (заголовок, username и аватар собеседника).
     * Параметры — id текущего пользователя (4 раза для подзапросов собеседника).
     */
    private static final String CHAT_COLUMNS = """
            c.id, c.kind,
            CASE
                WHEN c.title <> '' THEN c.title
                ELSE COALESCE((
                    SELECT u.nickname
                    FROM chat_members pm
                    JOIN users u ON u.id = pm.user_id
                    WHERE pm.chat_id = c.id AND pm.user_id <> ?
                    LIMIT 1
                ), 'Chat')
            END AS title,
            COALESCE((
                SELECT u.username
                FROM chat_members pm
                JOIN users u ON u.id = pm.user_id
                WHERE pm.chat_id = c.id AND pm.user_id <> ?
                LIMIT 1
            ), '') AS username,
            COALESCE((
                SELECT u.avatar_path
                FROM chat_members pm
                JOIN users u ON u.id = pm.user_id
                WHERE pm.chat_id = c.id AND pm.user_id <> ?
                LIMIT 1
            ), '') AS avatar_path,
            COALESCE((
                SELECT u.avatar_url
                FROM chat_members pm
                JOIN users u ON u.id = pm.user_id
                WHERE pm.chat_id = c.id AND pm.user_id <> ?
                LIMIT 1
            ), '') AS avatar_url,
            c.created_at,
            (
                SELECT pm.user_id
                FROM chat_members pm
                WHERE pm.chat_id = c.id AND pm.user_id <> ?
                LIMIT 1
            ) AS peer_user_id
            """;

    private final JdbcTemplate jdbc;

    public ChatRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public List<Chat> listForUser(UUID userId, int limit) {
        String me = userId.toString();
        return jdbc.query("""
                        SELECT %s
                        FROM chats c
                        JOIN chat_members m ON m.chat_id = c.id
                        WHERE m.user_id = ?
                        ORDER BY CASE WHEN c.kind = 'favorites' THEN 0 ELSE 1 END,
                                 COALESCE((
                                     SELECT MAX(msg.created_at) FROM messages msg WHERE msg.chat_id = c.id
                                 ), c.created_at) DESC, c.id DESC
                        LIMIT ?
                        """.formatted(CHAT_COLUMNS),
                CHAT_MAPPER, me, me, me, me, me, me, limit);
    }

    public Optional<Chat> findForUser(UUID chatId, UUID userId) {
        String me = userId.toString();
        List<Chat> chats = jdbc.query("""
                        SELECT %s
                        FROM chats c
                        WHERE c.id = ?
                          AND EXISTS (
                              SELECT 1 FROM chat_members m
                              WHERE m.chat_id = c.id AND m.user_id = ?
                          )
                        """.formatted(CHAT_COLUMNS),
                CHAT_MAPPER, me, me, me, me, me, chatId.toString(), me);
        return chats.stream().findFirst();
    }

    public boolean isMember(UUID chatId, UUID userId) {
        Integer count = jdbc.queryForObject("""
                        SELECT COUNT(1)
                        FROM chat_members
                        WHERE chat_id = ? AND user_id = ?
                        """,
                Integer.class, chatId.toString(), userId.toString());
        return count != null && count > 0;
    }

    public List<UUID> listMemberIds(UUID chatId) {
        return jdbc.query("""
                        SELECT user_id FROM chat_members WHERE chat_id = ?
                        """,
                (rs, rowNum) -> UUID.fromString(rs.getString("user_id")),
                chatId.toString());
    }

    /** Все собеседники пользователя (по всем чатам), без дубликатов. */
    public List<UUID> listPeerUserIds(UUID userId) {
        String me = userId.toString();
        return jdbc.query("""
                        SELECT DISTINCT pm.user_id
                        FROM chat_members m
                        JOIN chat_members pm ON pm.chat_id = m.chat_id AND pm.user_id <> m.user_id
                        WHERE m.user_id = ?
                        """,
                (rs, rowNum) -> UUID.fromString(rs.getString("user_id")),
                me);
    }

    /** Личный чат ровно между двумя пользователями, если он уже есть. */
    public Optional<Chat> findDirectBetween(UUID firstUserId, UUID secondUserId) {
        String me = firstUserId.toString();
        List<Chat> chats = jdbc.query("""
                        SELECT %s
                        FROM chats c
                        JOIN chat_members m1 ON m1.chat_id = c.id AND m1.user_id = ?
                        JOIN chat_members m2 ON m2.chat_id = c.id AND m2.user_id = ?
                        WHERE c.kind = 'direct'
                        LIMIT 1
                        """.formatted(CHAT_COLUMNS),
                CHAT_MAPPER,
                me, me, me, me, me,
                me,
                secondUserId.toString());
        return chats.stream().findFirst();
    }

    public void createDirect(UUID chatId, UUID firstUserId, UUID secondUserId, Instant createdAt) {
        long epochMillis = createdAt.toEpochMilli();
        jdbc.update("""
                        INSERT INTO chats (id, kind, title, created_at)
                        VALUES (?, 'direct', '', ?)
                        """,
                chatId.toString(), epochMillis);
        jdbc.update("""
                        INSERT INTO chat_members (chat_id, user_id, joined_at)
                        VALUES (?, ?, ?)
                        """,
                chatId.toString(), firstUserId.toString(), epochMillis);
        jdbc.update("""
                        INSERT INTO chat_members (chat_id, user_id, joined_at)
                        VALUES (?, ?, ?)
                        """,
                chatId.toString(), secondUserId.toString(), epochMillis);
    }

    /** Специальный чат заданного типа, уже существующий у пользователя. */
    public Optional<Chat> findByKindForUser(UUID userId, String kind) {
        String me = userId.toString();
        List<Chat> chats = jdbc.query("""
                        SELECT %s
                        FROM chats c
                        JOIN chat_members m ON m.chat_id = c.id
                        WHERE m.user_id = ? AND c.kind = ?
                        LIMIT 1
                        """.formatted(CHAT_COLUMNS),
                CHAT_MAPPER, me, me, me, me, me, me, kind);
        return chats.stream().findFirst();
    }

    /** Избранное: один участник — владелец, заголовок хранится в чате. */
    public void createFavorites(UUID chatId, UUID userId, String title, Instant createdAt) {
        long epochMillis = createdAt.toEpochMilli();
        jdbc.update("""
                        INSERT INTO chats (id, kind, title, owner_id, created_at)
                        VALUES (?, 'favorites', ?, ?, ?)
                        """,
                chatId.toString(), title, userId.toString(), epochMillis);
        jdbc.update("""
                        INSERT INTO chat_members (chat_id, user_id, joined_at)
                        VALUES (?, ?, ?)
                        """,
                chatId.toString(), userId.toString(), epochMillis);
    }
}

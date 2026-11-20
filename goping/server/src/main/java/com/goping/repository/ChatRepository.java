package com.goping.repository;

import com.goping.domain.Chat;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

/**
 * Чаты пользователя. Пока таблицы пустые — возвращается пустой список,
 * и клиент показывает заглушку.
 */
@Repository
public class ChatRepository {

    private static final RowMapper<Chat> CHAT_MAPPER = (rs, rowNum) -> new Chat(
            UUID.fromString(rs.getString("id")),
            rs.getString("kind"),
            rs.getString("title"),
            Instant.ofEpochMilli(rs.getLong("created_at"))
    );

    private final JdbcTemplate jdbc;

    public ChatRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public List<Chat> listForUser(UUID userId, int limit) {
        return jdbc.query("""
                        SELECT c.id, c.kind, c.title, c.created_at
                        FROM chats c
                        JOIN chat_members m ON m.chat_id = c.id
                        WHERE m.user_id = ?
                        ORDER BY c.created_at DESC, c.id DESC
                        LIMIT ?
                        """,
                CHAT_MAPPER, userId.toString(), limit);
    }
}

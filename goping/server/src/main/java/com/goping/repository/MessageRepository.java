package com.goping.repository;

import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;

import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.UUID;

/**
 * Сообщения хранятся только в зашифрованном виде (ciphertext + nonce + mac).
 * Расшифровка — в {@code service.MessageService}.
 */
@Repository
public class MessageRepository {

    public record EncryptedMessage(
            UUID id,
            UUID chatId,
            UUID senderId,
            String senderUsername,
            String senderNickname,
            String senderAvatarPath,
            String senderAvatarUrl,
            String ciphertext,
            String nonce,
            String mac,
            String algorithm,
            Instant createdAt,
            UUID replyToId,
            String replyCiphertext,
            String replyNonce,
            String replyMac,
            String replySenderNickname
    ) {
    }

    private static final RowMapper<EncryptedMessage> MESSAGE_MAPPER = (rs, rowNum) -> {
        String replyTo = rs.getString("reply_to_id");
        return new EncryptedMessage(
                UUID.fromString(rs.getString("id")),
                UUID.fromString(rs.getString("chat_id")),
                UUID.fromString(rs.getString("sender_id")),
                rs.getString("sender_username"),
                rs.getString("sender_nickname"),
                rs.getString("sender_avatar_path"),
                rs.getString("sender_avatar_url"),
                rs.getString("ciphertext"),
                rs.getString("nonce"),
                rs.getString("mac"),
                rs.getString("algorithm"),
                Instant.ofEpochMilli(rs.getLong("created_at")),
                replyTo == null || replyTo.isBlank() ? null : UUID.fromString(replyTo),
                rs.getString("reply_ciphertext"),
                rs.getString("reply_nonce"),
                rs.getString("reply_mac"),
                rs.getString("reply_sender_nickname")
        );
    };

    private static final String SELECT_WITH_REPLY = """
            SELECT m.id, m.chat_id, m.sender_id, m.ciphertext, m.nonce, m.mac,
                   m.algorithm, m.created_at, m.reply_to_id,
                   u.username AS sender_username,
                   u.nickname AS sender_nickname,
                   u.avatar_path AS sender_avatar_path,
                   u.avatar_url AS sender_avatar_url,
                   r.ciphertext AS reply_ciphertext,
                   r.nonce AS reply_nonce,
                   r.mac AS reply_mac,
                   ru.nickname AS reply_sender_nickname
            FROM messages m
            JOIN users u ON u.id = m.sender_id
            LEFT JOIN messages r ON r.id = m.reply_to_id
            LEFT JOIN users ru ON ru.id = r.sender_id
            """;

    private final JdbcTemplate jdbc;

    public MessageRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public void insert(UUID id,
                       UUID chatId,
                       UUID senderId,
                       String ciphertext,
                       String nonce,
                       String mac,
                       String algorithm,
                       Instant createdAt,
                       UUID replyToId) {
        jdbc.update("""
                        INSERT INTO messages
                            (id, chat_id, sender_id, ciphertext, nonce, mac, algorithm, created_at, reply_to_id)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                        """,
                id.toString(),
                chatId.toString(),
                senderId.toString(),
                ciphertext,
                nonce,
                mac,
                algorithm,
                createdAt.toEpochMilli(),
                replyToId == null ? null : replyToId.toString());
    }

    public Optional<EncryptedMessage> findById(UUID messageId) {
        List<EncryptedMessage> rows = jdbc.query(
                SELECT_WITH_REPLY + " WHERE m.id = ?",
                MESSAGE_MAPPER, messageId.toString());
        return rows.stream().findFirst();
    }

    /** Самое свежее сообщение чата, видимое пользователю (без скрытых для него). */
    public Optional<EncryptedMessage> findLatestForChat(UUID chatId, UUID viewerId) {
        List<EncryptedMessage> rows = jdbc.query(
                SELECT_WITH_REPLY + """
                        WHERE m.chat_id = ?
                          AND NOT EXISTS (
                              SELECT 1 FROM message_hides h
                              WHERE h.message_id = m.id AND h.user_id = ?
                          )
                        ORDER BY m.created_at DESC, m.id DESC
                        LIMIT 1
                        """,
                MESSAGE_MAPPER, chatId.toString(), viewerId.toString());
        return rows.stream().findFirst();
    }

    /**
     * Последние {@code limit} сообщений в хронологическом порядке.
     * Скрытые для viewer пропускаются.
     */
    public List<EncryptedMessage> listForChat(UUID chatId, UUID viewerId, int limit) {
        return jdbc.query("""
                        SELECT *
                        FROM (
                            """ + SELECT_WITH_REPLY + """
                            WHERE m.chat_id = ?
                              AND NOT EXISTS (
                                  SELECT 1 FROM message_hides h
                                  WHERE h.message_id = m.id AND h.user_id = ?
                              )
                            ORDER BY m.created_at DESC, m.id DESC
                            LIMIT ?
                        )
                        ORDER BY created_at ASC, id ASC
                        """,
                MESSAGE_MAPPER, chatId.toString(), viewerId.toString(), limit);
    }

    public void delete(UUID messageId) {
        jdbc.update("DELETE FROM message_hides WHERE message_id = ?", messageId.toString());
        jdbc.update("UPDATE messages SET reply_to_id = NULL WHERE reply_to_id = ?", messageId.toString());
        jdbc.update("DELETE FROM messages WHERE id = ?", messageId.toString());
    }

    public void hide(UUID userId, UUID messageId, Instant at) {
        jdbc.update("""
                        INSERT OR IGNORE INTO message_hides (user_id, message_id, created_at)
                        VALUES (?, ?, ?)
                        """,
                userId.toString(), messageId.toString(), at.toEpochMilli());
    }

    public void upsertRead(UUID chatId, UUID userId, UUID lastMessageId, Instant at) {
        jdbc.update("""
                        INSERT INTO chat_reads (chat_id, user_id, last_read_message_id, last_read_at)
                        VALUES (?, ?, ?, ?)
                        ON CONFLICT(chat_id, user_id) DO UPDATE SET
                            last_read_message_id = excluded.last_read_message_id,
                            last_read_at = excluded.last_read_at
                        """,
                chatId.toString(),
                userId.toString(),
                lastMessageId == null ? null : lastMessageId.toString(),
                at.toEpochMilli());
    }

    public Optional<Instant> peerReadAt(UUID chatId, UUID viewerId) {
        List<Instant> rows = jdbc.query("""
                        SELECT last_read_at
                        FROM chat_reads
                        WHERE chat_id = ? AND user_id <> ?
                        ORDER BY last_read_at DESC
                        LIMIT 1
                        """,
                (rs, rowNum) -> Instant.ofEpochMilli(rs.getLong("last_read_at")),
                chatId.toString(), viewerId.toString());
        return rows.stream().findFirst();
    }
}

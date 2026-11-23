package com.goping.repository;

import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowCallbackHandler;
import org.springframework.stereotype.Repository;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/** Poll rows. Vote identity is one row per (poll, user), with the option replaceable. */
@Repository
public class PollRepository {

    public record PollRow(String id, String chatId, String creatorId, String question,
                          boolean closed, String createdAt) {
    }

    public record OptionRow(String id, String pollId, int position, String text) {
    }

    private final JdbcTemplate jdbc;

    public PollRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public void create(String id, String chatId, String creatorId, String question, String createdAt) {
        jdbc.update("""
                        INSERT INTO polls (id, chat_id, creator_id, question, closed, created_at)
                        VALUES (?, ?, ?, ?, 0, ?)
                        """,
                id, chatId, creatorId, question, createdAt);
    }

    public void addOption(String id, String pollId, int position, String text) {
        jdbc.update("""
                        INSERT INTO poll_options (id, poll_id, position, text)
                        VALUES (?, ?, ?, ?)
                        """,
                id, pollId, position, text);
    }

    public Optional<PollRow> findById(String pollId) {
        List<PollRow> rows = jdbc.query("""
                        SELECT id, chat_id, creator_id, question, closed, created_at
                        FROM polls
                        WHERE id = ?
                        """,
                (rs, rowNum) -> new PollRow(
                        rs.getString("id"),
                        rs.getString("chat_id"),
                        rs.getString("creator_id"),
                        rs.getString("question"),
                        rs.getInt("closed") != 0,
                        rs.getString("created_at")),
                pollId);
        return rows.stream().findFirst();
    }

    public List<OptionRow> options(String pollId) {
        return jdbc.query("""
                        SELECT id, poll_id, position, text
                        FROM poll_options
                        WHERE poll_id = ?
                        ORDER BY position ASC, id ASC
                        """,
                (rs, rowNum) -> new OptionRow(
                        rs.getString("id"),
                        rs.getString("poll_id"),
                        rs.getInt("position"),
                        rs.getString("text")),
                pollId);
    }

    public boolean hasOption(String pollId, String optionId) {
        Integer count = jdbc.queryForObject("""
                        SELECT COUNT(1)
                        FROM poll_options
                        WHERE poll_id = ? AND id = ?
                        """,
                Integer.class, pollId, optionId);
        return count != null && count > 0;
    }

    public void upsertVote(String pollId, String optionId, String userId, String createdAt) {
        jdbc.update("""
                        INSERT INTO poll_votes (poll_id, option_id, user_id, created_at)
                        VALUES (?, ?, ?, ?)
                        ON CONFLICT(poll_id, user_id) DO UPDATE SET
                            option_id = excluded.option_id,
                            created_at = excluded.created_at
                        """,
                pollId, optionId, userId, createdAt);
    }

    public Map<String, Integer> voteCounts(String pollId) {
        Map<String, Integer> counts = new LinkedHashMap<>();
        jdbc.query("""
                        SELECT option_id, COUNT(1) AS votes
                        FROM poll_votes
                        WHERE poll_id = ?
                        GROUP BY option_id
                        """,
                (RowCallbackHandler) rs -> counts.put(rs.getString("option_id"), rs.getInt("votes")),
                pollId);
        return counts;
    }

    public String myVote(String pollId, String userId) {
        List<String> rows = jdbc.query("""
                        SELECT option_id
                        FROM poll_votes
                        WHERE poll_id = ? AND user_id = ?
                        """,
                (rs, rowNum) -> rs.getString("option_id"),
                pollId, userId);
        return rows.stream().findFirst().orElse(null);
    }
}

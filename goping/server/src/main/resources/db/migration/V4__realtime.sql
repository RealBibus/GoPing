-- Ответ на сообщение + скрытие «для себя» + прочитано.
ALTER TABLE messages ADD COLUMN reply_to_id TEXT;

CREATE TABLE IF NOT EXISTS message_hides (
    user_id    TEXT    NOT NULL,
    message_id TEXT    NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, message_id)
);

CREATE INDEX IF NOT EXISTS message_hides_msg_idx ON message_hides (message_id);

CREATE TABLE IF NOT EXISTS chat_reads (
    chat_id               TEXT    NOT NULL,
    user_id               TEXT    NOT NULL,
    last_read_message_id  TEXT,
    last_read_at          INTEGER NOT NULL,
    PRIMARY KEY (chat_id, user_id)
);

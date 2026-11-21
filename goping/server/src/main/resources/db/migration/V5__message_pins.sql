-- Закреплённые сообщения.
-- scope = 'me'   — видно только пользователю, который закрепил;
-- scope = 'chat' — видно всем участникам чата.
CREATE TABLE IF NOT EXISTS message_pins (
    message_id TEXT    NOT NULL REFERENCES messages (id) ON DELETE CASCADE,
    user_id    TEXT    NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    scope      TEXT    NOT NULL CHECK (scope IN ('me', 'chat')),
    created_at INTEGER NOT NULL,
    PRIMARY KEY (message_id, user_id, scope)
);

CREATE INDEX IF NOT EXISTS message_pins_msg_idx ON message_pins (message_id);
CREATE INDEX IF NOT EXISTS message_pins_user_idx ON message_pins (user_id);

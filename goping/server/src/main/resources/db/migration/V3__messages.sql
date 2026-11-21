-- Сообщения чатов.
-- Текст в открытом виде не хранится: ciphertext зашифрован
-- ГОСТ Р 34.12-2015 «Кузнечик» в режиме CTR (ГОСТ Р 34.13-2015),
-- mac — HMAC-SHA256 (encrypt-then-MAC). nonce уникален для каждого
-- сообщения и хранится рядом с шифротекстом.
CREATE TABLE IF NOT EXISTS messages (
    id         TEXT PRIMARY KEY,
    chat_id    TEXT    NOT NULL REFERENCES chats (id) ON DELETE CASCADE,
    sender_id  TEXT    NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    ciphertext TEXT    NOT NULL,
    nonce      TEXT    NOT NULL,
    mac        TEXT    NOT NULL,
    algorithm  TEXT    NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS messages_chat_idx
    ON messages (chat_id, created_at, id);

CREATE INDEX IF NOT EXISTS messages_sender_idx
    ON messages (sender_id);

-- Пользователи.
-- username и email хранятся в нижнем регистре, уникальны.
-- nickname НЕ уникален: это отображаемое имя.
CREATE TABLE IF NOT EXISTS users (
    id            TEXT PRIMARY KEY,
    username      TEXT    NOT NULL,
    nickname      TEXT    NOT NULL,
    email         TEXT    NOT NULL,
    password_hash TEXT    NOT NULL,
    bio           TEXT    NOT NULL DEFAULT '',
    avatar_path   TEXT,
    created_at    INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS users_username_uq ON users (username);
CREATE UNIQUE INDEX IF NOT EXISTS users_email_uq ON users (email);

-- Refresh-сессии. В базе лежит только SHA-256 от токена.
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id             TEXT PRIMARY KEY,
    user_id        TEXT    NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    token_hash     TEXT    NOT NULL,
    expires_at     INTEGER NOT NULL,
    created_at     INTEGER NOT NULL,
    revoked_at     INTEGER,
    replaced_by_id TEXT,
    user_agent     TEXT    NOT NULL DEFAULT '',
    ip             TEXT    NOT NULL DEFAULT ''
);

CREATE UNIQUE INDEX IF NOT EXISTS refresh_tokens_hash_uq ON refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS refresh_tokens_user_idx ON refresh_tokens (user_id);

-- Чаты. Пока пусто: клиент получает пустой список и показывает заглушку.
CREATE TABLE IF NOT EXISTS chats (
    id         TEXT PRIMARY KEY,
    kind       TEXT    NOT NULL DEFAULT 'direct',
    title      TEXT    NOT NULL DEFAULT '',
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS chat_members (
    chat_id   TEXT    NOT NULL REFERENCES chats (id) ON DELETE CASCADE,
    user_id   TEXT    NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    joined_at INTEGER NOT NULL,
    PRIMARY KEY (chat_id, user_id)
);

CREATE INDEX IF NOT EXISTS chat_members_user_idx ON chat_members (user_id);

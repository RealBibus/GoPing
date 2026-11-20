-- GoPing: вход через GitHub и одноразовые серверные токены.
-- Колонки email/password_hash остаются с первой версии схемы ради
-- совместимости существующих баз; новый вход их не использует.

ALTER TABLE users ADD COLUMN avatar_url TEXT;
ALTER TABLE users ADD COLUMN github_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS users_github_id_uq
    ON users (github_id)
    WHERE github_id IS NOT NULL;

-- Серверные токены вида GOPING-XXXXXXXXXXXX.
-- В базе лежит только SHA-256; сырой токен видит только пользователь.
CREATE TABLE IF NOT EXISTS server_tokens (
    id            TEXT PRIMARY KEY,
    token_hash    TEXT    NOT NULL,
    token_prefix  TEXT    NOT NULL,
    label         TEXT    NOT NULL DEFAULT '',
    user_id       TEXT REFERENCES users (id) ON DELETE CASCADE,
    created_at    INTEGER NOT NULL,
    activated_at  INTEGER,
    last_used_at  INTEGER,
    revoked_at    INTEGER
);

CREATE UNIQUE INDEX IF NOT EXISTS server_tokens_hash_uq ON server_tokens (token_hash);
CREATE INDEX IF NOT EXISTS server_tokens_user_idx ON server_tokens (user_id);

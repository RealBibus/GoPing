-- Polls and their votes. Poll messages are stored in the encrypted messages table.
CREATE TABLE IF NOT EXISTS polls (
    id         TEXT PRIMARY KEY,
    chat_id    TEXT NOT NULL,
    creator_id TEXT NOT NULL,
    question   TEXT NOT NULL,
    closed     INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS poll_votes (
    poll_id    TEXT NOT NULL,
    option_id  TEXT NOT NULL,
    user_id    TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (poll_id, user_id)
);

CREATE TABLE IF NOT EXISTS poll_options (
    id       TEXT PRIMARY KEY,
    poll_id  TEXT NOT NULL,
    position INTEGER NOT NULL,
    text     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS poll_options_poll_idx ON poll_options (poll_id, position);
CREATE INDEX IF NOT EXISTS poll_votes_poll_idx ON poll_votes (poll_id, option_id);

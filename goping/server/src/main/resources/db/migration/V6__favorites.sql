-- Избранное: один серверный чат на пользователя.
-- Владелец хранится в chats.owner_id, чтобы partial unique index
-- гарантировал уникальность чата даже при параллельном создании.

ALTER TABLE chats ADD COLUMN owner_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS chats_favorites_owner_idx
    ON chats (owner_id)
    WHERE kind = 'favorites';

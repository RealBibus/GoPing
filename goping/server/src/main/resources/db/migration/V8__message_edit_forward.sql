-- Редактирование и пересылка сообщений
ALTER TABLE messages ADD COLUMN edited_at INTEGER;
ALTER TABLE messages ADD COLUMN forwarded_from_sender_nickname TEXT;
ALTER TABLE messages ADD COLUMN forwarded_from_text TEXT;
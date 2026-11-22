-- Тип сообщения: text (обычное) или call (системное уведомление о звонке).
ALTER TABLE messages ADD COLUMN kind TEXT NOT NULL DEFAULT 'text';

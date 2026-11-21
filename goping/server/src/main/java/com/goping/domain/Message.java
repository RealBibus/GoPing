package com.goping.domain;

import java.time.Instant;
import java.util.UUID;

/**
 * Сообщение чата в расшифрованном виде — только для отдачи участникам чата.
 * В базе лежит шифротекст, см. {@code repository.MessageRepository}.
 */
public record Message(
        UUID id,
        UUID chatId,
        UUID senderId,
        String senderUsername,
        String senderNickname,
        String senderAvatarPath,
        String senderAvatarUrl,
        String text,
        String algorithm,
        Instant createdAt,
        UUID replyToId,
        String replyToText,
        String replyToSenderNickname,
        String deliveryStatus,
        String pinScope
) {
}

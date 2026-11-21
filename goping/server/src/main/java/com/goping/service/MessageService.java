package com.goping.service;

import com.goping.config.DeletePolicy;
import com.goping.crypto.MessageCipher;
import com.goping.domain.Message;
import com.goping.domain.User;
import com.goping.error.ApiException;
import com.goping.error.ValidationException;
import com.goping.repository.ChatRepository;
import com.goping.repository.MessageRepository;
import com.goping.repository.UserRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.security.GeneralSecurityException;
import java.time.Instant;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/**
 * Отправка и чтение сообщений. Текст шифруется ГОСТ Р 34.12-2015
 * («Кузнечик», CTR) до записи в SQLite и расшифровывается только для
 * участников чата.
 */
@Service
public class MessageService {

    private static final Logger log = LoggerFactory.getLogger(MessageService.class);
    private static final String DAMAGED_TEXT = "[сообщение повреждено: не удалось проверить подпись]";

    private final ChatRepository chats;
    private final MessageRepository messages;
    private final UserRepository users;
    private final MessageCipher cipher;
    private final DeletePolicy deletePolicy;

    public MessageService(ChatRepository chats,
                          MessageRepository messages,
                          UserRepository users,
                          MessageCipher cipher,
                          DeletePolicy deletePolicy) {
        this.chats = chats;
        this.messages = messages;
        this.users = users;
        this.cipher = cipher;
        this.deletePolicy = deletePolicy;
    }

    public Message send(UUID userId, UUID chatId, String rawText, String rawReplyToId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }

        Validation.Checked checked = Validation.message(rawText);
        if (checked.hasError()) {
            throw new ValidationException(Map.of("text", checked.error()));
        }

        UUID replyToId = null;
        if (rawReplyToId != null && !rawReplyToId.isBlank()) {
            try {
                replyToId = UUID.fromString(rawReplyToId.trim());
            } catch (IllegalArgumentException e) {
                throw new ValidationException(Map.of("replyToId", "некорректный идентификатор"));
            }
            MessageRepository.EncryptedMessage reply = messages.findById(replyToId)
                    .orElseThrow(() -> new ValidationException(Map.of("replyToId", "сообщение не найдено")));
            if (!reply.chatId().equals(chatId)) {
                throw new ValidationException(Map.of("replyToId", "ответ только внутри этого чата"));
            }
        }

        User sender = users.findById(userId).orElseThrow(ApiException::notFound);
        MessageCipher.Sealed sealed = cipher.seal(checked.value());
        UUID id = UUID.randomUUID();
        Instant createdAt = Instant.now();

        messages.insert(
                id,
                chatId,
                userId,
                encode(sealed.ciphertext()),
                encode(sealed.nonce()),
                encode(sealed.mac()),
                MessageCipher.ALGORITHM,
                createdAt,
                replyToId
        );

        return messages.findById(id)
                .map(stored -> decrypt(stored, userId, null))
                .orElseThrow(ApiException::notFound);
    }

    public List<Message> list(UUID userId, UUID chatId, int limit) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }

        int safeLimit = Math.max(1, Math.min(limit, 100));
        Map<UUID, String> pinScopes = messages.pinScopes(chatId, userId);
        return messages.listForChat(chatId, userId, safeLimit).stream()
                .map(stored -> decrypt(stored, userId, pinScopes.get(stored.id())))
                .toList();
    }

    /** Расшифрованное превью последнего видимого сообщения или null. */
    public Message latestForChat(UUID chatId, UUID viewerId) {
        return messages.findLatestForChat(chatId, viewerId)
                .map(stored -> decrypt(stored, viewerId, null))
                .orElse(null);
    }

    @Transactional
    public DeleteResult delete(UUID userId, UUID chatId, UUID messageId, boolean forEveryone) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        MessageRepository.EncryptedMessage stored = messages.findById(messageId)
                .orElseThrow(ApiException::notFound);
        if (!stored.chatId().equals(chatId)) {
            throw ApiException.notFound();
        }
        if (!stored.senderId().equals(userId) && !deletePolicy.isAllowDeleteOthers()) {
            throw new ApiException(HttpStatus.FORBIDDEN, "DELETE_OTHERS_DISABLED",
                    "Удаление чужих сообщений отключено на сервере");
        }

        if (forEveryone) {
            messages.delete(messageId);
            return new DeleteResult(true, null);
        }

        messages.hide(userId, messageId, Instant.now());
        return new DeleteResult(false, userId);
    }

    /** scope: 'me' — только для себя, 'chat' — для себя и собеседника. */
    @Transactional
    public PinResult setPin(UUID userId, UUID chatId, UUID messageId, String scope, boolean pinned) {
        if (!"me".equals(scope) && !"chat".equals(scope)) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_PIN_SCOPE",
                    "Некорректная область закрепления");
        }
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        MessageRepository.EncryptedMessage stored = messages.findById(messageId)
                .orElseThrow(ApiException::notFound);
        if (!stored.chatId().equals(chatId)) {
            throw ApiException.notFound();
        }

        if (pinned) {
            messages.pin(userId, messageId, scope, Instant.now());
        } else {
            messages.unpin(userId, messageId, scope);
        }
        return new PinResult(messageId, scope, userId, pinned);
    }

    public ReadResult markRead(UUID userId, UUID chatId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        Message latest = latestForChat(chatId, userId);
        Instant at = Instant.now();
        UUID lastId = latest == null ? null : latest.id();
        messages.upsertRead(chatId, userId, lastId, at);
        return new ReadResult(lastId, at);
    }

    private Message decrypt(MessageRepository.EncryptedMessage stored, UUID viewerId, String pinScope) {
        String text = openText(stored.ciphertext(), stored.nonce(), stored.mac(), stored.id(), stored.chatId());

        String replyText = null;
        if (stored.replyToId() != null
                && stored.replyCiphertext() != null
                && stored.replyNonce() != null
                && stored.replyMac() != null) {
            replyText = openText(
                    stored.replyCiphertext(),
                    stored.replyNonce(),
                    stored.replyMac(),
                    stored.replyToId(),
                    stored.chatId());
            if (replyText.length() > 120) {
                replyText = replyText.substring(0, 119) + "…";
            }
        }

        String status = "sent";
        if (stored.senderId().equals(viewerId)) {
            Instant peerRead = messages.peerReadAt(stored.chatId(), viewerId).orElse(null);
            if (peerRead != null && !peerRead.isBefore(stored.createdAt())) {
                status = "read";
            }
        }

        return new Message(
                stored.id(),
                stored.chatId(),
                stored.senderId(),
                stored.senderUsername(),
                stored.senderNickname(),
                stored.senderAvatarPath(),
                stored.senderAvatarUrl(),
                text,
                stored.algorithm(),
                stored.createdAt(),
                stored.replyToId(),
                replyText,
                stored.replySenderNickname(),
                status,
                pinScope
        );
    }

    private String openText(String ciphertext, String nonce, String mac, UUID id, UUID chatId) {
        try {
            return cipher.open(decode(ciphertext), decode(nonce), decode(mac));
        } catch (GeneralSecurityException | IllegalArgumentException e) {
            log.warn("message {} in chat {} failed integrity check: {}", id, chatId, e.getMessage());
            return DAMAGED_TEXT;
        }
    }

    private static String encode(byte[] value) {
        return Base64.getEncoder().encodeToString(value);
    }

    private static byte[] decode(String value) {
        return Base64.getDecoder().decode(value);
    }

    public record DeleteResult(boolean forEveryone, UUID onlyForUser) {
    }

    public record PinResult(UUID messageId, String scope, UUID actorId, boolean pinned) {
    }

    public record ReadResult(UUID lastReadMessageId, Instant readAt) {
    }
}

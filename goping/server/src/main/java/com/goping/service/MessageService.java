package com.goping.service;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.config.DeletePolicy;
import com.goping.config.LiveLimits;
import com.goping.crypto.MessageCipher;
import com.goping.domain.Message;
import com.goping.domain.User;
import com.goping.error.ApiException;
import com.goping.error.ValidationException;
import com.goping.media.MediaStore;
import com.goping.repository.ChatRepository;
import com.goping.repository.MessageRepository;
import com.goping.repository.UserRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.io.IOException;
import java.security.GeneralSecurityException;
import java.time.Instant;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
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
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final Set<String> MEDIA_KINDS = Set.of("photo", "video", "file");

    private final ChatRepository chats;
    private final MessageRepository messages;
    private final UserRepository users;
    private final MessageCipher cipher;
    private final DeletePolicy deletePolicy;
    private final Validation validation;
    private final LiveLimits limits;

    public MessageService(ChatRepository chats,
                          MessageRepository messages,
                          UserRepository users,
                          MessageCipher cipher,
                          DeletePolicy deletePolicy,
                          Validation validation,
                          LiveLimits limits) {
        this.chats = chats;
        this.messages = messages;
        this.users = users;
        this.cipher = cipher;
        this.deletePolicy = deletePolicy;
        this.validation = validation;
        this.limits = limits;
    }

    public Message send(UUID userId, UUID chatId, String rawText, String rawReplyToId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }

        Validation.Checked checked = validation.message(rawText);
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

        return storeMessage(userId, chatId, checked.value(), "text", createdNow(), replyToId);
    }

    /** Системное сообщение о звонке в чат (видно обоим участникам). */
    public Message sendCallLog(UUID chatId, UUID actorUserId, String callStatus, long durationSec) {
        if (!chats.isMember(chatId, actorUserId)) {
            throw ApiException.notFound();
        }
        String safeStatus = switch (callStatus == null ? "" : callStatus) {
            case "ended", "missed", "declined", "cancelled" -> callStatus;
            default -> "ended";
        };
        long safeDuration = Math.max(0, durationSec);
        String payload = "{\"status\":\"" + safeStatus + "\",\"durationSec\":" + safeDuration + "}";
        return storeMessage(actorUserId, chatId, payload, "call", createdNow(), null);
    }

    /** Голосовое сообщение: текст хранит JSON с путём к файлу и длительностью. */
    public Message sendVoice(UUID userId, UUID chatId, String voicePath, int durationSec, String rawReplyToId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        if (voicePath == null || voicePath.isBlank()) {
            throw new ValidationException(Map.of("audio", "Голосовое сообщение пустое"));
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
        int safeDuration = Math.max(0, Math.min(durationSec, limits.get().maxVoiceSeconds()));
        String safePath = voicePath.replace("\\", "/");
        String payload = "{\"path\":\"" + safePath.replace("\"", "") + "\",\"durationSec\":" + safeDuration + "}";
        return storeMessage(userId, chatId, payload, "voice", createdNow(), replyToId);
    }

    /** Creates an encrypted media message from a file already stored by MediaStore. */
    public Message sendMedia(UUID userId, UUID chatId, String kind, MediaStore.StoredMedia media) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        String safeKind = kind == null ? "" : kind.toLowerCase(Locale.ROOT);
        if (!MEDIA_KINDS.contains(safeKind)) {
            throw new ValidationException(Map.of("kind", "kind must be photo, video or file"));
        }
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("url", media.path());
        payload.put("fileName", media.fileName());
        payload.put("sizeBytes", media.sizeBytes());
        payload.put("mime", media.mime());
        try {
            return storeMessage(userId, chatId, JSON.writeValueAsString(payload), safeKind, createdNow(), null);
        } catch (IOException e) {
            throw new IllegalStateException("failed to build media payload", e);
        }
    }

    /** Creates an encrypted payload message such as a poll card. */
    public Message storePayloadMessage(UUID userId, UUID chatId, String payload, String kind) {
        return storeMessage(userId, chatId, payload, kind, createdNow(), null);
    }

    @Transactional
    public Message edit(UUID userId, UUID chatId, UUID messageId, String rawText) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        Validation.Checked checked = validation.message(rawText);
        if (checked.hasError()) {
            throw new ValidationException(Map.of("text", checked.error()));
        }
        MessageRepository.EncryptedMessage stored = messages.findById(messageId)
                .orElseThrow(ApiException::notFound);
        if (!stored.chatId().equals(chatId)) {
            throw ApiException.notFound();
        }
        if (!stored.senderId().equals(userId)) {
            throw new ApiException(HttpStatus.FORBIDDEN, "FORBIDDEN", "Редактировать можно только свои сообщения");
        }
        if (!"text".equals(stored.kind())) {
            throw new ValidationException(Map.of("message", "Редактировать можно только текстовые сообщения"));
        }

        MessageCipher.Sealed sealed = cipher.seal(checked.value());
        Instant editedAt = Instant.now();
        messages.updateContent(messageId, encode(sealed.ciphertext()), encode(sealed.nonce()), encode(sealed.mac()), editedAt);
        return messages.findById(messageId)
                .map(m -> decrypt(m, userId, null))
                .orElseThrow(ApiException::notFound);
    }

    @Transactional
    public Message forward(UUID userId, UUID targetChatId, UUID fromChatId, UUID messageId) {
        if (!chats.isMember(targetChatId, userId)) {
            throw ApiException.notFound();
        }
        if (!chats.isMember(fromChatId, userId)) {
            throw ApiException.notFound();
        }
        MessageRepository.EncryptedMessage stored = messages.findById(messageId)
                .orElseThrow(ApiException::notFound);
        if (!stored.chatId().equals(fromChatId)) {
            throw ApiException.notFound();
        }

        String originalText = openText(stored.ciphertext(), stored.nonce(), stored.mac(), stored.id(), stored.chatId());
        if (DAMAGED_TEXT.equals(originalText)) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "MESSAGE_DAMAGED", "Нельзя переслать повреждённое сообщение");
        }

        String forwardedNick = stored.senderNickname() == null ? "" : stored.senderNickname();
        String preview = forwardPreview(stored.kind(), originalText);
        if (preview.length() > 300) {
            preview = preview.substring(0, 299) + "…";
        }

        return storeMessage(userId, targetChatId, originalText, stored.kind(), createdNow(), null, forwardedNick, preview);
    }

    /** Forward previews are human sentences, never raw JSON payloads. */
    private static String forwardPreview(String kind, String originalText) {
        String safeKind = kind == null ? "" : kind;
        return switch (safeKind) {
            case "voice" -> "Voice message";
            case "photo" -> "Photo";
            case "video" -> "Video";
            case "file" -> "File";
            case "call" -> callPreview(originalText);
            case "poll" -> pollPreview(originalText);
            default -> originalText;
        };
    }

    private static String callPreview(String originalText) {
        String status = "";
        try {
            JsonNode node = JSON.readTree(originalText);
            status = node.path("status").asText("");
        } catch (Exception ignored) {
            // Fall back to a voice call when the stored payload cannot be parsed.
        }
        return switch (status) {
            case "missed", "declined", "cancelled" -> "Missed call";
            default -> "Voice call";
        };
    }

    private static String pollPreview(String originalText) {
        String question = "";
        try {
            JsonNode node = JSON.readTree(originalText);
            question = node.path("question").asText("").strip();
        } catch (Exception ignored) {
            // Fall back to a generic label when the stored payload cannot be parsed.
        }
        return question.isEmpty() ? "Poll" : "Poll: " + question;
    }

    private Message storeMessage(UUID userId, UUID chatId, String text, String kind, Instant createdAt, UUID replyToId) {
        return storeMessage(userId, chatId, text, kind, createdAt, replyToId, null, null);
    }

    private Message storeMessage(UUID userId, UUID chatId, String text, String kind, Instant createdAt, UUID replyToId,
                                 String forwardedFromSenderNickname, String forwardedFromText) {
        MessageCipher.Sealed sealed = cipher.seal(text);
        UUID id = UUID.randomUUID();
        messages.insert(
                id,
                chatId,
                userId,
                encode(sealed.ciphertext()),
                encode(sealed.nonce()),
                encode(sealed.mac()),
                MessageCipher.ALGORITHM,
                kind,
                createdAt,
                replyToId,
                forwardedFromSenderNickname,
                forwardedFromText
        );
        return messages.findById(id)
                .map(stored -> decrypt(stored, userId, null))
                .orElseThrow(ApiException::notFound);
    }

    private static Instant createdNow() {
        return Instant.now();
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

    /** Закреплённые сообщения чата (все, не только из окна последних сообщений). */
    public List<Message> pinned(UUID userId, UUID chatId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        Map<UUID, String> pinScopes = messages.pinScopes(chatId, userId);
        return messages.pinnedForChat(chatId, userId).stream()
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

    /** Unread messages for chat list badges. */
    public int unreadCount(UUID chatId, UUID viewerId) {
        return messages.unreadCount(chatId, viewerId);
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
                stored.kind(),
                stored.createdAt(),
                stored.replyToId(),
                replyText,
                stored.replySenderNickname(),
                status,
                pinScope,
                stored.editedAt(),
                stored.forwardedFromSenderNickname(),
                stored.forwardedFromText()
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

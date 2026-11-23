package com.goping.service;

import com.goping.domain.Chat;
import com.goping.domain.Message;
import com.goping.domain.User;
import com.goping.error.ApiException;
import com.goping.error.ValidationException;
import com.goping.repository.ChatRepository;
import com.goping.repository.UserRepository;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/**
 * Список чатов и создание личного чата по username собеседника.
 */
@Service
public class ChatService {

    private static final int PREVIEW_MAX = 80;
    private static final String FAVORITES_KIND = "favorites";
    private static final String FAVORITES_TITLE = "Favorites";

    private final ChatRepository chats;
    private final UserRepository users;
    private final MessageService messages;
    private final PresenceService presence;

    public ChatService(ChatRepository chats,
                       UserRepository users,
                       MessageService messages,
                       PresenceService presence) {
        this.chats = chats;
        this.users = users;
        this.messages = messages;
        this.presence = presence;
    }

    @Transactional
    public List<EnrichedChat> list(UUID userId, int limit) {
        int safeLimit = Math.max(1, Math.min(limit, 100));
        presence.heartbeat(userId);
        ensureFavorites(userId);
        List<Chat> found = chats.listForUser(userId, safeLimit);
        List<EnrichedChat> result = new ArrayList<>(found.size());
        for (Chat chat : found) {
            result.add(enrich(chat, userId));
        }
        return result;
    }

    public EnrichedChat getForUser(UUID chatId, UUID userId) {
        Chat chat = chats.findForUser(chatId, userId).orElseThrow(ApiException::notFound);
        return enrich(chat, userId);
    }

    /**
     * Создаёт личный чат или возвращает уже существующий. Повторный вызов
     * с тем же username идемпотентен.
     */
    @Transactional
    public EnrichedChat createDirect(UUID userId, String rawUsername) {
        String username = Validation.normalizeUsername(rawUsername);
        if (username.isEmpty()) {
            throw new ValidationException(Map.of("username", "укажите имя пользователя"));
        }

        User peer = users.findByUsername(username).orElseThrow(() -> new ApiException(
                HttpStatus.NOT_FOUND, "NOT_FOUND", "Пользователь не найден"));
        if (peer.id().equals(userId)) {
            throw new ValidationException(Map.of("username", "нельзя создать чат с самим собой"));
        }

        Chat existing = chats.findDirectBetween(userId, peer.id()).orElse(null);
        if (existing != null) {
            return enrich(existing, userId);
        }

        UUID chatId = UUID.randomUUID();
        chats.createDirect(chatId, userId, peer.id(), Instant.now());
        Chat created = chats.findForUser(chatId, userId).orElseThrow(ApiException::notFound);
        return enrich(created, userId);
    }

    /** Создаёт серверный чат «Избранное» один раз и возвращает его при следующих вызовах. */
    @Transactional
    public EnrichedChat ensureFavorites(UUID userId) {
        Chat existing = chats.findByKindForUser(userId, FAVORITES_KIND).orElse(null);
        if (existing != null) {
            return enrich(existing, userId);
        }

        UUID chatId = UUID.randomUUID();
        chats.createFavorites(chatId, userId, FAVORITES_TITLE, Instant.now());
        Chat created = chats.findForUser(chatId, userId).orElseThrow(ApiException::notFound);
        return enrich(created, userId);
    }

    public void markTyping(UUID userId, UUID chatId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
        presence.setTyping(chatId, userId);
    }

    public List<UUID> listPeerIds(UUID userId) {
        return chats.listPeerUserIds(userId);
    }

    private EnrichedChat enrich(Chat chat, UUID viewerId) {
        Message latest = messages.latestForChat(chat.id(), viewerId);
        String preview = latest == null ? null : previewText(latest);
        Instant lastAt = latest == null ? null : latest.createdAt();

        UUID peerId = chat.peerUserId();
        boolean online = peerId != null && presence.isOnline(peerId);
        Instant lastSeen = peerId == null ? null : presence.lastSeenAt(peerId);
        boolean typing = presence.peerTyping(chat.id(), viewerId);

        return new EnrichedChat(chat, preview, lastAt, online, lastSeen, typing);
    }

    private static String previewText(Message message) {
        if (message == null) return null;
        if ("call".equals(message.kind())) {
            return callPreview(message.text());
        }
        if ("voice".equals(message.kind())) {
            return "Voice message";
        }
        return previewText(message.text());
    }

    private static String callPreview(String payload) {
        String status = "ended";
        long sec = 0;
        if (payload != null) {
            if (payload.contains("\"status\":\"missed\"")) status = "missed";
            else if (payload.contains("\"status\":\"declined\"")) status = "declined";
            else if (payload.contains("\"status\":\"cancelled\"")) status = "missed";
            else if (payload.contains("\"status\":\"ended\"")) status = "ended";
            java.util.regex.Matcher m = java.util.regex.Pattern.compile("\"durationSec\"\\s*:\\s*(\\d+)").matcher(payload);
            if (m.find()) {
                try { sec = Long.parseLong(m.group(1)); } catch (NumberFormatException ignored) { }
            }
        }
        return switch (status) {
            case "missed" -> "Missed call";
            case "declined" -> "Call declined";
            default -> sec > 0 ? "Call ended · " + formatDuration(sec) : "Call ended";
        };
    }

    private static String formatDuration(long sec) {
        long h = sec / 3600;
        long m = (sec % 3600) / 60;
        long s = sec % 60;
        if (h > 0) return String.format("%d:%02d:%02d", h, m, s);
        return String.format("%d:%02d", m, s);
    }

    private static String previewText(String text) {
        if (text == null) {
            return null;
        }
        String flat = text.replace('\n', ' ').replace('\r', ' ').trim();
        if (flat.length() <= PREVIEW_MAX) {
            return flat;
        }
        return flat.substring(0, PREVIEW_MAX - 1) + "…";
    }

    public record EnrichedChat(
            Chat chat,
            String lastMessageText,
            Instant lastMessageAt,
            boolean peerOnline,
            Instant peerLastSeenAt,
            boolean peerTyping
    ) {
    }
}

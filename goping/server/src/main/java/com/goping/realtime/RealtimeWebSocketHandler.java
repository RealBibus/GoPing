package com.goping.realtime;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.service.ChatService;
import com.goping.service.MessageService;
import com.goping.service.PresenceService;
import com.goping.web.DtoMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;
import org.springframework.web.socket.CloseStatus;
import org.springframework.web.socket.TextMessage;
import org.springframework.web.socket.WebSocketSession;
import org.springframework.web.socket.handler.TextWebSocketHandler;

import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

@Component
public class RealtimeWebSocketHandler extends TextWebSocketHandler {

    public static final String ATTR_USER_ID = "goping.userId";

    private static final Logger log = LoggerFactory.getLogger(RealtimeWebSocketHandler.class);

    private final RealtimeHub hub;
    private final PresenceService presence;
    private final ChatService chats;
    private final MessageService messages;
    private final DtoMapper mapper;
    private final ObjectMapper json;

    public RealtimeWebSocketHandler(RealtimeHub hub,
                                    PresenceService presence,
                                    ChatService chats,
                                    MessageService messages,
                                    DtoMapper mapper,
                                    ObjectMapper json) {
        this.hub = hub;
        this.presence = presence;
        this.chats = chats;
        this.messages = messages;
        this.mapper = mapper;
        this.json = json;
    }

    @Override
    public void afterConnectionEstablished(WebSocketSession session) {
        UUID userId = (UUID) session.getAttributes().get(ATTR_USER_ID);
        if (userId == null) {
            try {
                session.close(CloseStatus.NOT_ACCEPTABLE);
            } catch (Exception ignored) {
            }
            return;
        }
        hub.register(userId, session);
        presence.heartbeat(userId);
        broadcastPresence(userId, true);
        log.debug("ws connected user={}", userId);
    }

    @Override
    public void afterConnectionClosed(WebSocketSession session, CloseStatus status) {
        UUID userId = (UUID) session.getAttributes().get(ATTR_USER_ID);
        if (userId == null) {
            return;
        }
        hub.unregister(userId, session);
        if (!hub.isConnected(userId)) {
            presence.markOffline(userId);
            broadcastPresence(userId, false);
        }
        log.debug("ws closed user={} status={}", userId, status);
    }

    @Override
    protected void handleTextMessage(WebSocketSession session, TextMessage message) throws Exception {
        UUID userId = (UUID) session.getAttributes().get(ATTR_USER_ID);
        if (userId == null) {
            return;
        }
        JsonNode root = json.readTree(message.getPayload());
        String type = root.path("type").asText("");
        switch (type) {
            case "ping" -> session.sendMessage(new TextMessage("{\"type\":\"pong\"}"));
            case "typing" -> handleTyping(userId, root);
            case "read" -> handleRead(userId, root);
            default -> log.debug("unknown ws type={}", type);
        }
    }

    private void handleTyping(UUID userId, JsonNode root) {
        String chatIdRaw = root.path("chatId").asText(null);
        if (chatIdRaw == null || chatIdRaw.isBlank()) {
            return;
        }
        UUID chatId;
        try {
            chatId = UUID.fromString(chatIdRaw);
        } catch (IllegalArgumentException e) {
            return;
        }
        try {
            chats.markTyping(userId, chatId);
        } catch (Exception e) {
            return;
        }
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "typing");
        event.put("chatId", chatId.toString());
        event.put("userId", userId.toString());
        hub.sendToChatExcept(chatId, userId, event);
    }

    private void handleRead(UUID userId, JsonNode root) {
        String chatIdRaw = root.path("chatId").asText(null);
        if (chatIdRaw == null || chatIdRaw.isBlank()) {
            return;
        }
        UUID chatId;
        try {
            chatId = UUID.fromString(chatIdRaw);
        } catch (IllegalArgumentException e) {
            return;
        }
        try {
            MessageService.ReadResult read = messages.markRead(userId, chatId);
            if (read == null) {
                return;
            }
            Map<String, Object> event = new LinkedHashMap<>();
            event.put("type", "messages.read");
            event.put("chatId", chatId.toString());
            event.put("readerId", userId.toString());
            event.put("readAt", read.readAt().toString());
            if (read.lastReadMessageId() != null) {
                event.put("upToMessageId", read.lastReadMessageId().toString());
            }
            hub.sendToChat(chatId, event);
        } catch (Exception e) {
            log.debug("read failed: {}", e.getMessage());
        }
    }

    private void broadcastPresence(UUID userId, boolean online) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "presence");
        event.put("userId", userId.toString());
        event.put("online", online);
        Instant lastSeen = presence.lastSeenAt(userId);
        if (lastSeen != null) {
            event.put("lastSeenAt", lastSeen.toString());
        }
        // Рассылаем всем, у кого есть чат с этим юзером — упрощённо всем онлайн-сессиям
        // через чаты слишком тяжело без индекса; шлём в его чаты.
        for (var enriched : chats.list(userId, 100)) {
            UUID peer = enriched.chat().peerUserId();
            if (peer != null) {
                hub.sendToUser(peer, event);
            }
        }
    }
}

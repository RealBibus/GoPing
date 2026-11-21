package com.goping.realtime;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.repository.ChatRepository;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;
import org.springframework.web.socket.TextMessage;
import org.springframework.web.socket.WebSocketSession;

import java.io.IOException;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArraySet;

/**
 * Раздаёт JSON-события подключённым клиентам. Только дельты, без полного снапшота.
 */
@Component
public class RealtimeHub {

    private static final Logger log = LoggerFactory.getLogger(RealtimeHub.class);

    private final ConcurrentHashMap<UUID, CopyOnWriteArraySet<WebSocketSession>> sessions =
            new ConcurrentHashMap<>();
    private final ObjectMapper mapper;
    private final ChatRepository chats;

    public RealtimeHub(ObjectMapper mapper, ChatRepository chats) {
        this.mapper = mapper;
        this.chats = chats;
    }

    public void register(UUID userId, WebSocketSession session) {
        sessions.computeIfAbsent(userId, ignored -> new CopyOnWriteArraySet<>()).add(session);
    }

    public void unregister(UUID userId, WebSocketSession session) {
        CopyOnWriteArraySet<WebSocketSession> set = sessions.get(userId);
        if (set == null) {
            return;
        }
        set.remove(session);
        if (set.isEmpty()) {
            sessions.remove(userId, set);
        }
    }

    public boolean isConnected(UUID userId) {
        CopyOnWriteArraySet<WebSocketSession> set = sessions.get(userId);
        return set != null && !set.isEmpty();
    }

    public void sendToUser(UUID userId, Map<String, Object> event) {
        CopyOnWriteArraySet<WebSocketSession> set = sessions.get(userId);
        if (set == null || set.isEmpty()) {
            return;
        }
        String json;
        try {
            json = mapper.writeValueAsString(event);
        } catch (IOException e) {
            log.warn("failed to serialize ws event: {}", e.getMessage());
            return;
        }
        TextMessage message = new TextMessage(json);
        for (WebSocketSession session : set) {
            send(session, message);
        }
    }

    public void sendToChat(UUID chatId, Map<String, Object> event) {
        List<UUID> members = chats.listMemberIds(chatId);
        for (UUID memberId : members) {
            sendToUser(memberId, event);
        }
    }

    public void sendToChatExcept(UUID chatId, UUID exceptUserId, Map<String, Object> event) {
        List<UUID> members = chats.listMemberIds(chatId);
        for (UUID memberId : members) {
            if (!memberId.equals(exceptUserId)) {
                sendToUser(memberId, event);
            }
        }
    }

    private void send(WebSocketSession session, TextMessage message) {
        if (!session.isOpen()) {
            return;
        }
        try {
            synchronized (session) {
                session.sendMessage(message);
            }
        } catch (IOException e) {
            log.debug("ws send failed: {}", e.getMessage());
        }
    }
}

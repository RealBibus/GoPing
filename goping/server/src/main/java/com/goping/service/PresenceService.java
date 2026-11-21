package com.goping.service;

import org.springframework.stereotype.Service;

import java.time.Duration;
import java.time.Instant;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Онлайн и «печатает». Онлайн = активная WS-сессия (или свежий heartbeat).
 */
@Service
public class PresenceService {

    private static final Duration HEARTBEAT_TTL = Duration.ofSeconds(25);
    private static final Duration TYPING_TTL = Duration.ofSeconds(5);

    private final Set<UUID> socketOnline = ConcurrentHashMap.newKeySet();
    private final ConcurrentHashMap<UUID, Instant> lastSeen = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<UUID, Instant> heartbeatUntil = new ConcurrentHashMap<>();
    /** chatId → (userId → typingUntil) */
    private final ConcurrentHashMap<UUID, ConcurrentHashMap<UUID, Instant>> typing =
            new ConcurrentHashMap<>();

    public void setSocketOnline(UUID userId) {
        socketOnline.add(userId);
        lastSeen.put(userId, Instant.now());
    }

    public void markOffline(UUID userId) {
        socketOnline.remove(userId);
        heartbeatUntil.remove(userId);
        lastSeen.put(userId, Instant.now());
    }

    public void heartbeat(UUID userId) {
        lastSeen.put(userId, Instant.now());
        heartbeatUntil.put(userId, Instant.now().plus(HEARTBEAT_TTL));
    }

    public void setTyping(UUID chatId, UUID userId) {
        heartbeat(userId);
        typing.computeIfAbsent(chatId, ignored -> new ConcurrentHashMap<>())
                .put(userId, Instant.now().plus(TYPING_TTL));
    }

    public boolean isOnline(UUID userId) {
        if (socketOnline.contains(userId)) {
            return true;
        }
        Instant until = heartbeatUntil.get(userId);
        return until != null && until.isAfter(Instant.now());
    }

    public Instant lastSeenAt(UUID userId) {
        return lastSeen.get(userId);
    }

    /** Печатает ли собеседник (не я) именно в этом чате. */
    public boolean peerTyping(UUID chatId, UUID me) {
        ConcurrentHashMap<UUID, Instant> byUser = typing.get(chatId);
        if (byUser == null || byUser.isEmpty()) {
            return false;
        }
        Instant now = Instant.now();
        for (Map.Entry<UUID, Instant> entry : byUser.entrySet()) {
            if (entry.getKey().equals(me)) {
                continue;
            }
            if (entry.getValue().isAfter(now)) {
                return true;
            }
        }
        return false;
    }
}

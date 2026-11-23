package com.goping.realtime;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.config.LiveLimits;
import com.goping.domain.Message;
import com.goping.repository.ChatRepository;
import com.goping.service.MessageService;
import com.goping.web.DtoMapper;
import jakarta.annotation.PostConstruct;
import jakarta.annotation.PreDestroy;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.context.annotation.Lazy;
import org.springframework.stereotype.Component;

import java.time.Duration;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Пересылка сигналов для 1:1-звонков. Состояние звонка живёт в памяти сервера:
 * сервер только передаёт offer/answer/ICE между двумя участниками чата.
 * При коротком обрыве связи клиент переподключается к WebSocket и шлёт
 * call.rejoin — сервер тихо уведомляет собеседника, тот инициирует ICE restart.
 */
@Component
public class CallSignalingService {

    private static final Logger log = LoggerFactory.getLogger(CallSignalingService.class);
    private static final Duration CALL_TTL = Duration.ofMinutes(10);

    private record CallState(UUID callId, UUID chatId, UUID callerId, UUID calleeId, Instant updatedAt, Instant acceptedAt) {
        CallState touch() {
            return new CallState(callId, chatId, callerId, calleeId, Instant.now(), acceptedAt);
        }

        CallState accepted() {
            return new CallState(callId, chatId, callerId, calleeId, Instant.now(), Instant.now());
        }

        boolean participates(UUID userId) {
            return callerId.equals(userId) || calleeId.equals(userId);
        }

        UUID peerOf(UUID userId) {
            return callerId.equals(userId) ? calleeId : callerId;
        }
    }

    private final RealtimeHub hub;
    private final ChatRepository chats;
    private final MessageService messages;
    private final RealtimeEvents events;
    private final DtoMapper mapper;
    private final ObjectMapper json;
    private final LiveLimits limits;
    private final ConcurrentHashMap<UUID, CallState> calls = new ConcurrentHashMap<>();
    private final ScheduledExecutorService sweeper = Executors.newSingleThreadScheduledExecutor(runnable -> {
        Thread thread = new Thread(runnable, "goping-call-sweeper");
        thread.setDaemon(true);
        return thread;
    });

    public CallSignalingService(RealtimeHub hub, ChatRepository chats, MessageService messages,
                                @Lazy RealtimeEvents events, DtoMapper mapper, ObjectMapper json,
                                LiveLimits limits) {
        this.hub = hub;
        this.chats = chats;
        this.messages = messages;
        this.events = events;
        this.mapper = mapper;
        this.json = json;
        this.limits = limits;
    }

    @PostConstruct
    public void startSweeper() {
        sweeper.scheduleWithFixedDelay(this::sweep, 10, 10, TimeUnit.SECONDS);
    }

    @PreDestroy
    public void stopSweeper() {
        sweeper.shutdownNow();
    }

    private void sweep() {
        try {
            expireStale();
            expireLongCalls();
        } catch (Exception e) {
            log.debug("call sweep failed: {}", e.getMessage());
        }
    }

    public void invite(UUID from, JsonNode root) {
        UUID chatId = uuid(root, "chatId");
        UUID callId = uuid(root, "callId");
        if (chatId == null || callId == null || !chats.isMember(chatId, from)) {
            log.info("call invite ignored from={} chatId={} callId={} member={}",
                    from, chatId, callId, chatId != null && chats.isMember(chatId, from));
            return;
        }

        Optional<UUID> peer = chats.listMemberIds(chatId).stream()
                .filter(id -> !id.equals(from))
                .findFirst();
        if (peer.isEmpty()) {
            return;
        }

        CallState call = new CallState(callId, chatId, from, peer.get(), Instant.now(), null);
        calls.put(callId, call);
        expireStale();
        expireLongCalls();

        log.info("call invite routed callId={} chatId={} from={} to={} calleeConnected={}",
                callId, chatId, from, peer.get(), hub.isConnected(peer.get()));
        Map<String, Object> event = event(call, "call.invite", from, null);
        hub.sendToUser(peer.get(), event);
    }

    public void accept(UUID from, JsonNode root) {
        withCall(from, root, (call, peer) -> {
            calls.put(call.callId(), call.accepted());
            hub.sendToUser(peer, event(call, "call.accepted", from, null));
        });
    }

    public void reject(UUID from, JsonNode root) {
        withCall(from, root, (call, peer) -> {
            hub.sendToUser(peer, event(call, "call.rejected", from, null));
            calls.remove(call.callId());
            logCall(call, from, "declined");
        });
    }

    public void end(UUID from, JsonNode root) {
        String reason = root.path("reason").asText("hangup");
        withCall(from, root, (call, peer) -> {
            Map<String, Object> event = event(call, "call.ended", from, null);
            event.put("reason", reason);
            hub.sendToUser(peer, event);
            calls.remove(call.callId());
            String status = call.acceptedAt() != null ? "ended" : "missed";
            logCall(call, from, status);
        });
    }

    public void signal(UUID from, JsonNode root) {
        String kind = root.path("kind").asText("");
        if (kind.isBlank()) {
            return;
        }
        withCall(from, root, (call, peer) -> {
            calls.put(call.callId(), call.touch());
            Map<String, Object> event = event(call, "call.signal", from, kind);
            JsonNode data = root.get("data");
            event.put("data", data == null || data.isNull() ? new LinkedHashMap<String, Object>() : data);
            hub.sendToUser(peer, event);
        });
    }

    /** Клиент вернулся после короткого обрыва WebSocket — просим собеседника сделать ICE restart. */
    public void rejoin(UUID from, JsonNode root) {
        withCall(from, root, (call, peer) -> {
            calls.put(call.callId(), call.touch());
            hub.sendToUser(peer, event(call, "call.peer-rejoined", from, null));
        });
    }

    /** Доставить входящие инвайты, которые ждали, пока пользователь был офлайн. */
    public void deliverPendingInvites(UUID userId) {
        expireStale();
        expireLongCalls();
        for (CallState call : calls.values()) {
            if (!call.calleeId().equals(userId)) continue;
            if (call.acceptedAt() != null) continue;
            if (call.updatedAt().isBefore(Instant.now().minus(CALL_TTL))) continue;
            Map<String, Object> event = event(call, "call.invite", call.callerId(), null);
            hub.sendToUser(userId, event);
            log.info("delivered pending call invite callId={} to={}", call.callId(), userId);
        }
    }

    private void withCall(UUID from, JsonNode root, java.util.function.BiConsumer<CallState, UUID> action) {
        UUID callId = uuid(root, "callId");
        if (callId == null) {
            log.info("call signal dropped: missing callId from={}", from);
            return;
        }
        CallState call = calls.get(callId);
        if (call == null || !call.participates(from)) {
            log.info("call signal orphaned callId={} from={}", callId, from);
            // Сервер мог перезапуститься — тихо закрываем осиротевший звонок.
            hub.sendToUser(from, orphanedEvent(callId, root.path("chatId").asText(null)));
            return;
        }
        try {
            action.accept(call, call.peerOf(from));
        } catch (Exception e) {
            log.debug("call signaling failed: {}", e.getMessage());
        }
    }

    private Map<String, Object> event(CallState call, String type, UUID from, String kind) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", type);
        event.put("callId", call.callId().toString());
        event.put("chatId", call.chatId().toString());
        event.put("fromUserId", from.toString());
        event.put("toUserId", call.peerOf(from).toString());
        if (kind != null) {
            event.put("kind", kind);
        }
        return event;
    }

    private Map<String, Object> orphanedEvent(UUID callId, String chatId) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "call.ended");
        event.put("callId", callId.toString());
        if (chatId != null && !chatId.isBlank()) {
            event.put("chatId", chatId);
        }
        event.put("reason", "server-restart");
        return event;
    }

    private void logCall(CallState call, UUID actorId, String status) {
        try {
            long durationSec = 0;
            if (call.acceptedAt() != null) {
                durationSec = Math.max(0, java.time.Duration.between(call.acceptedAt(), Instant.now()).getSeconds());
            }
            Message msg = messages.sendCallLog(call.chatId(), actorId, status, durationSec);
            events.messageCreated(call.chatId(), mapper.message(msg));
        } catch (Exception e) {
            log.warn("failed to persist call log callId={}: {}", call.callId(), e.getMessage());
        }
    }

    private void expireStale() {
        Instant threshold = Instant.now().minus(CALL_TTL);
        for (CallState call : calls.values()) {
            if (call.updatedAt().isBefore(threshold) && calls.remove(call.callId(), call)) {
                closeCall(call, "timeout", call.callerId());
            }
        }
    }

    /** maxCallMinutes = 0 disables this check. */
    private void expireLongCalls() {
        int maxMinutes = limits.get().maxCallMinutes();
        if (maxMinutes <= 0) {
            return;
        }
        Duration maxDuration = Duration.ofMinutes(maxMinutes);
        Instant now = Instant.now();
        for (CallState call : calls.values()) {
            Instant startedAt = call.acceptedAt() == null ? call.updatedAt() : call.acceptedAt();
            if (Duration.between(startedAt, now).compareTo(maxDuration) > 0
                    && calls.remove(call.callId(), call)) {
                closeCall(call, "limit", call.callerId());
            }
        }
    }

    private void closeCall(CallState call, String reason, UUID actorId) {
        for (UUID member : new UUID[]{call.callerId(), call.calleeId()}) {
            Map<String, Object> event = new LinkedHashMap<>();
            event.put("type", "call.ended");
            event.put("callId", call.callId().toString());
            event.put("chatId", call.chatId().toString());
            event.put("reason", reason);
            hub.sendToUser(member, event);
        }
        String status = call.acceptedAt() != null ? "ended" : "missed";
        logCall(call, actorId, status);
    }

    private static UUID uuid(JsonNode root, String field) {
        String raw = root.path(field).asText(null);
        if (raw == null || raw.isBlank()) {
            return null;
        }
        try {
            if (raw.length() == 32) {
                raw = raw.substring(0, 8) + "-" + raw.substring(8, 12) + "-"
                        + raw.substring(12, 16) + "-" + raw.substring(16, 20) + "-" + raw.substring(20);
            }
            return UUID.fromString(raw);
        } catch (IllegalArgumentException e) {
            return null;
        }
    }
}

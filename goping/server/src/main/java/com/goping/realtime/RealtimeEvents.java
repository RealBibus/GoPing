package com.goping.realtime;

import com.goping.repository.ChatRepository;
import com.goping.service.ChatService;
import com.goping.web.DtoMapper;
import com.goping.web.dto.ApiDtos;
import org.springframework.context.annotation.Lazy;
import org.springframework.stereotype.Component;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

@Component
public class RealtimeEvents {

    private final RealtimeHub hub;
    private final ChatRepository chats;
    private final ChatService chatService;
    private final DtoMapper mapper;

    public RealtimeEvents(RealtimeHub hub,
                          ChatRepository chats,
                          @Lazy ChatService chatService,
                          DtoMapper mapper) {
        this.hub = hub;
        this.chats = chats;
        this.chatService = chatService;
        this.mapper = mapper;
    }

    public void messageCreated(UUID chatId, ApiDtos.MessageDto message) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "message.created");
        event.put("chatId", chatId.toString());
        event.put("message", message);
        hub.sendToChat(chatId, event);
        pushChatUpdated(chatId);
    }

    public void messageEdited(UUID chatId, ApiDtos.MessageDto message) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "message.edited");
        event.put("chatId", chatId.toString());
        event.put("message", message);
        hub.sendToChat(chatId, event);
        pushChatUpdated(chatId);
    }

    public void messageDeleted(UUID chatId, UUID messageId, boolean forEveryone, UUID onlyForUser) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "message.deleted");
        event.put("chatId", chatId.toString());
        event.put("messageId", messageId.toString());
        event.put("forEveryone", forEveryone);
        if (forEveryone) {
            hub.sendToChat(chatId, event);
            pushChatUpdated(chatId);
        } else if (onlyForUser != null) {
            hub.sendToUser(onlyForUser, event);
            pushChatUpdatedForUser(chatId, onlyForUser);
        }
    }

    public void messagePinned(UUID chatId, UUID messageId, String scope, UUID actorId, boolean pinned) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "message.pinned");
        event.put("chatId", chatId.toString());
        event.put("messageId", messageId.toString());
        event.put("scope", scope);
        event.put("actorId", actorId.toString());
        event.put("pinned", pinned);

        if ("me".equals(scope)) {
            hub.sendToUser(actorId, event);
        } else {
            hub.sendToChat(chatId, event);
        }
    }

    public void chatCreated(UUID chatId) {
        pushChatUpdated(chatId);
    }

    public void typing(UUID chatId, UUID userId) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "typing");
        event.put("chatId", chatId.toString());
        event.put("userId", userId.toString());
        hub.sendToChatExcept(chatId, userId, event);
    }

    public void messagesRead(UUID chatId, UUID readerId, UUID upToMessageId, java.time.Instant readAt) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("type", "messages.read");
        event.put("chatId", chatId.toString());
        event.put("readerId", readerId.toString());
        event.put("readAt", readAt.toString());
        if (upToMessageId != null) {
            event.put("upToMessageId", upToMessageId.toString());
        }
        hub.sendToChat(chatId, event);
    }

    private void pushChatUpdated(UUID chatId) {
        for (UUID memberId : chats.listMemberIds(chatId)) {
            pushChatUpdatedForUser(chatId, memberId);
        }
    }

    private void pushChatUpdatedForUser(UUID chatId, UUID userId) {
        try {
            ApiDtos.ChatDto dto = mapper.chat(chatService.getForUser(chatId, userId));
            Map<String, Object> event = new LinkedHashMap<>();
            event.put("type", "chat.updated");
            event.put("chat", dto);
            hub.sendToUser(userId, event);
        } catch (Exception ignored) {
            // чат мог исчезнуть
        }
    }
}

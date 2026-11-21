package com.goping.web;

import com.goping.domain.Message;
import com.goping.error.ApiException;
import com.goping.realtime.RealtimeEvents;
import com.goping.security.RequestContext;
import com.goping.service.ChatService;
import com.goping.service.MessageService;
import com.goping.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;
import java.util.UUID;

@RestController
@RequestMapping("/api/v1/chats")
public class ChatController {

    private final ChatService chats;
    private final MessageService messages;
    private final DtoMapper mapper;
    private final RealtimeEvents realtime;

    public ChatController(ChatService chats,
                          MessageService messages,
                          DtoMapper mapper,
                          RealtimeEvents realtime) {
        this.chats = chats;
        this.messages = messages;
        this.mapper = mapper;
        this.realtime = realtime;
    }

    @GetMapping
    public ApiDtos.ChatListResponse list(
            @RequestParam(defaultValue = "30") int limit,
            HttpServletRequest request) {
        List<ChatService.EnrichedChat> found = chats.list(RequestContext.userId(request), limit);
        List<ApiDtos.ChatDto> items = found.stream().map(mapper::chat).toList();
        return new ApiDtos.ChatListResponse(items, null);
    }

    @PostMapping
    public ResponseEntity<ApiDtos.ChatDto> create(
            @RequestBody(required = false) ApiDtos.CreateChatRequest body,
            HttpServletRequest request) {
        String username = body == null ? null : body.username();
        ChatService.EnrichedChat chat = chats.createDirect(RequestContext.userId(request), username);
        realtime.chatCreated(chat.chat().id());
        return ResponseEntity.status(HttpStatus.CREATED).body(mapper.chat(chat));
    }

    @PostMapping("/{chatId}/typing")
    public ResponseEntity<Void> typing(@PathVariable String chatId, HttpServletRequest request) {
        UUID id = parseChatId(chatId);
        UUID userId = RequestContext.userId(request);
        chats.markTyping(userId, id);
        realtime.typing(id, userId);
        return ResponseEntity.noContent().build();
    }

    @PostMapping("/{chatId}/read")
    public ResponseEntity<Void> read(@PathVariable String chatId, HttpServletRequest request) {
        UUID id = parseChatId(chatId);
        UUID userId = RequestContext.userId(request);
        MessageService.ReadResult result = messages.markRead(userId, id);
        realtime.messagesRead(id, userId, result.lastReadMessageId(), result.readAt());
        return ResponseEntity.noContent().build();
    }

    @GetMapping("/{chatId}/messages")
    public ApiDtos.MessageListResponse messages(
            @PathVariable String chatId,
            @RequestParam(defaultValue = "50") int limit,
            HttpServletRequest request) {
        List<Message> found = messages.list(
                RequestContext.userId(request), parseChatId(chatId), limit);
        List<ApiDtos.MessageDto> items = found.stream().map(mapper::message).toList();
        return new ApiDtos.MessageListResponse(items, null);
    }

    @PostMapping("/{chatId}/messages")
    public ResponseEntity<ApiDtos.MessageDto> send(
            @PathVariable String chatId,
            @RequestBody(required = false) ApiDtos.SendMessageRequest body,
            HttpServletRequest request) {
        String text = body == null ? null : body.text();
        String replyToId = body == null ? null : body.replyToId();
        UUID id = parseChatId(chatId);
        Message message = messages.send(
                RequestContext.userId(request), id, text, replyToId);
        ApiDtos.MessageDto dto = mapper.message(message);
        realtime.messageCreated(id, dto);
        return ResponseEntity.status(HttpStatus.CREATED).body(dto);
    }

    @DeleteMapping("/{chatId}/messages/{messageId}")
    public ResponseEntity<Void> delete(
            @PathVariable String chatId,
            @PathVariable String messageId,
            @RequestBody(required = false) ApiDtos.DeleteMessageRequest body,
            HttpServletRequest request) {
        boolean forEveryone = body != null && body.forEveryone();
        UUID chat = parseChatId(chatId);
        UUID msg = parseId(messageId, "сообщения");
        MessageService.DeleteResult result = messages.delete(
                RequestContext.userId(request), chat, msg, forEveryone);
        realtime.messageDeleted(chat, msg, result.forEveryone(), result.onlyForUser());
        return ResponseEntity.noContent().build();
    }

    @PostMapping("/{chatId}/messages/{messageId}/pin")
    public ResponseEntity<Void> pin(
            @PathVariable String chatId,
            @PathVariable String messageId,
            @RequestBody(required = false) ApiDtos.PinRequest body,
            HttpServletRequest request) {
        String scope = body == null ? null : body.scope();
        UUID chat = parseChatId(chatId);
        UUID msg = parseId(messageId, "сообщения");
        MessageService.PinResult result = messages.setPin(
                RequestContext.userId(request), chat, msg, scope, true);
        realtime.messagePinned(chat, result.messageId(), result.scope(),
                result.actorId(), result.pinned());
        return ResponseEntity.noContent().build();
    }

    @DeleteMapping("/{chatId}/messages/{messageId}/pin")
    public ResponseEntity<Void> unpin(
            @PathVariable String chatId,
            @PathVariable String messageId,
            @RequestBody(required = false) ApiDtos.PinRequest body,
            HttpServletRequest request) {
        String scope = body == null ? null : body.scope();
        UUID chat = parseChatId(chatId);
        UUID msg = parseId(messageId, "сообщения");
        MessageService.PinResult result = messages.setPin(
                RequestContext.userId(request), chat, msg, scope, false);
        realtime.messagePinned(chat, result.messageId(), result.scope(),
                result.actorId(), result.pinned());
        return ResponseEntity.noContent().build();
    }

    private static UUID parseChatId(String raw) {
        return parseId(raw, "чата");
    }

    private static UUID parseId(String raw, String what) {
        try {
            return UUID.fromString(raw);
        } catch (IllegalArgumentException e) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_ID",
                    "Некорректный идентификатор " + what);
        }
    }
}

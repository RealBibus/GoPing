package com.goping.web;

import com.goping.domain.Chat;
import com.goping.domain.Message;
import com.goping.domain.User;
import com.goping.media.MediaStore;
import com.goping.service.AuthService;
import com.goping.service.ChatService;
import com.goping.service.PresenceService;
import com.goping.web.dto.ApiDtos;
import com.goping.web.dto.UserDto;
import org.springframework.stereotype.Component;

import java.time.Duration;

/**
 * Преобразование доменных объектов в DTO.
 */
@Component
public class DtoMapper {

    private final MediaStore media;
    private final PresenceService presence;

    public DtoMapper(MediaStore media, PresenceService presence) {
        this.media = media;
        this.presence = presence;
    }

    public UserDto user(User user) {
        return new UserDto(
                user.id().toString(),
                user.username(),
                user.nickname(),
                user.bio(),
                media.url(user.avatarPath(), user.avatarUrl()),
                user.createdAt(),
                user.updatedAt(),
                presence.isOnline(user.id()),
                presence.lastSeenAt(user.id())
        );
    }

    public ApiDtos.AuthResponse auth(AuthService.AuthResult result) {
        long expiresIn = Math.max(0, Duration.between(java.time.Instant.now(), result.expiresAt()).toSeconds());
        return new ApiDtos.AuthResponse(
                user(result.user()),
                "Bearer",
                result.accessToken(),
                result.refreshToken(),
                expiresIn,
                result.expiresAt()
        );
    }

    public ApiDtos.ChatDto chat(ChatService.EnrichedChat enriched) {
        Chat chat = enriched.chat();
        return new ApiDtos.ChatDto(
                chat.id().toString(),
                chat.kind(),
                chat.title(),
                chat.username(),
                media.url(chat.avatarPath(), chat.avatarUrl()),
                chat.createdAt(),
                chat.peerUserId() == null ? null : chat.peerUserId().toString(),
                enriched.lastMessageText(),
                enriched.lastMessageAt(),
                enriched.peerOnline(),
                enriched.peerLastSeenAt(),
                enriched.peerTyping()
        );
    }

    public ApiDtos.MessageDto message(Message message) {
        ApiDtos.ReplyPreviewDto reply = null;
        if (message.replyToId() != null) {
            reply = new ApiDtos.ReplyPreviewDto(
                    message.replyToId().toString(),
                    message.replyToText() == null ? "" : message.replyToText(),
                    message.replyToSenderNickname() == null ? "" : message.replyToSenderNickname()
            );
        }
        return new ApiDtos.MessageDto(
                message.id().toString(),
                message.chatId().toString(),
                new ApiDtos.MessageSenderDto(
                        message.senderId().toString(),
                        message.senderUsername(),
                        message.senderNickname(),
                        media.url(message.senderAvatarPath(), message.senderAvatarUrl())
                ),
                message.text(),
                message.algorithm(),
                message.createdAt(),
                reply,
                message.deliveryStatus()
        );
    }
}

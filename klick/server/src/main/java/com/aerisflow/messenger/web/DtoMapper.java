package com.aerisflow.messenger.web;

import com.aerisflow.messenger.domain.Chat;
import com.aerisflow.messenger.domain.User;
import com.aerisflow.messenger.media.MediaStore;
import com.aerisflow.messenger.service.AuthService;
import com.aerisflow.messenger.web.dto.ApiDtos;
import com.aerisflow.messenger.web.dto.UserDto;
import org.springframework.stereotype.Component;

import java.time.Duration;

/**
 * Преобразование доменных объектов в DTO.
 */
@Component
public class DtoMapper {

    private final MediaStore media;

    public DtoMapper(MediaStore media) {
        this.media = media;
    }

    public UserDto user(User user, boolean includeEmail) {
        return new UserDto(
                user.id().toString(),
                user.username(),
                user.nickname(),
                includeEmail ? user.email() : null,
                user.bio(),
                media.url(user.avatarPath()),
                user.createdAt(),
                user.updatedAt()
        );
    }

    public ApiDtos.AuthResponse auth(AuthService.AuthResult result) {
        long expiresIn = Math.max(0, Duration.between(java.time.Instant.now(), result.expiresAt()).toSeconds());
        return new ApiDtos.AuthResponse(
                user(result.user(), true),
                "Bearer",
                result.accessToken(),
                result.refreshToken(),
                expiresIn,
                result.expiresAt()
        );
    }

    public ApiDtos.ChatDto chat(Chat chat) {
        return new ApiDtos.ChatDto(
                chat.id().toString(),
                chat.kind(),
                chat.title(),
                chat.createdAt()
        );
    }
}

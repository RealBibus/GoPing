package com.goping.web;

import com.goping.domain.Chat;
import com.goping.domain.User;
import com.goping.media.MediaStore;
import com.goping.service.AuthService;
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

    public DtoMapper(MediaStore media) {
        this.media = media;
    }

    public UserDto user(User user) {
        return new UserDto(
                user.id().toString(),
                user.username(),
                user.nickname(),
                user.bio(),
                media.url(user.avatarPath(), user.avatarUrl()),
                user.createdAt(),
                user.updatedAt()
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

    public ApiDtos.ChatDto chat(Chat chat) {
        return new ApiDtos.ChatDto(
                chat.id().toString(),
                chat.kind(),
                chat.title(),
                chat.createdAt()
        );
    }
}

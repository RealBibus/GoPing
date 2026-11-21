package com.goping.service;

import com.goping.domain.User;
import com.goping.error.ApiException;
import com.goping.error.ValidationException;
import com.goping.media.MediaStore;
import com.goping.repository.UserRepository;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.web.multipart.MultipartFile;

import java.io.IOException;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

/**
 * Профиль: никнейм (без уникальности), username (с проверкой занятости),
 * описание и аватарка.
 */
@Service
public class UserService {

    private final UserRepository users;
    private final MediaStore media;

    public record UpdateProfileInput(String nickname, String username, String bio) {
    }

    public record UsernameStatus(String username, boolean available, String reason) {
    }

    public UserService(UserRepository users, MediaStore media) {
        this.users = users;
        this.media = media;
    }

    public User get(UUID userId) {
        return users.findById(userId).orElseThrow(ApiException::notFound);
    }

    /** Публичный профиль пользователя по username. */
    public User getByUsername(String rawUsername) {
        String username = Validation.normalizeUsername(rawUsername);
        return users.findByUsername(username).orElseThrow(() -> new ApiException(
                HttpStatus.NOT_FOUND, "NOT_FOUND", "Пользователь не найден"));
    }

    public User updateProfile(UUID userId, UpdateProfileInput input) {
        if (input.nickname() == null && input.username() == null && input.bio() == null) {
            throw new ValidationException(Map.of("body", "нет полей для обновления"));
        }

        Map<String, String> errors = new LinkedHashMap<>();
        String nickname = null;
        String username = null;
        String bio = null;

        if (input.nickname() != null) {
            Validation.Checked checked = Validation.nickname(input.nickname());
            if (checked.hasError()) {
                errors.put("nickname", checked.error());
            } else {
                nickname = checked.value();
            }
        }

        if (input.username() != null) {
            Validation.Checked checked = Validation.username(input.username());
            if (checked.hasError()) {
                errors.put("username", checked.error());
            } else {
                username = checked.value();
            }
        }

        if (input.bio() != null) {
            Validation.Checked checked = Validation.bio(input.bio());
            if (checked.hasError()) {
                errors.put("bio", checked.error());
            } else {
                bio = checked.value();
            }
        }

        if (!errors.isEmpty()) {
            throw new ValidationException(errors);
        }

        if (username != null && users.usernameExists(username)) {
            User current = get(userId);
            if (current.username().equals(username)) {
                username = null;
            } else {
                throw ApiException.usernameTaken();
            }
        }

        if (nickname == null && username == null && bio == null) {
            return get(userId);
        }

        return users.updateProfile(userId, new UserRepository.ProfilePatch(nickname, username, bio), Instant.now())
                .orElseThrow(ApiException::notFound);
    }

    public UsernameStatus checkUsername(String raw) {
        String username = Validation.normalizeUsername(raw);
        String problem = Validation.usernameProblem(raw);
        if (!problem.isEmpty()) {
            return new UsernameStatus(username, false, problem);
        }
        if (users.usernameExists(username)) {
            return new UsernameStatus(username, false, "taken");
        }
        return new UsernameStatus(username, true, null);
    }

    public User setAvatar(UUID userId, MultipartFile file) {
        User current = get(userId);
        String avatarPath;
        try {
            avatarPath = media.saveAvatar(file);
        } catch (IOException e) {
            throw new ApiException(HttpStatus.INTERNAL_SERVER_ERROR, "INTERNAL", "Не удалось сохранить файл");
        }

        User updated = users.setAvatar(userId, avatarPath, Instant.now())
                .orElseThrow(ApiException::notFound);
        media.delete(current.avatarPath());
        return updated;
    }

    public User deleteAvatar(UUID userId) {
        User current = get(userId);
        User updated = users.clearAvatar(userId, Instant.now()).orElseThrow(ApiException::notFound);
        media.delete(current.avatarPath());
        return updated;
    }
}

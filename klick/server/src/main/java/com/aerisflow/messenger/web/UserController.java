package com.aerisflow.messenger.web;

import com.aerisflow.messenger.domain.User;
import com.aerisflow.messenger.error.ApiException;
import com.aerisflow.messenger.error.ValidationException;
import com.aerisflow.messenger.security.RequestContext;
import com.aerisflow.messenger.service.UserService;
import com.aerisflow.messenger.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PatchMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RequestPart;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.multipart.MultipartFile;

import java.util.Map;
import java.util.UUID;

@RestController
@RequestMapping("/api/v1/users")
public class UserController {

    private final UserService users;
    private final DtoMapper mapper;

    public UserController(UserService users, DtoMapper mapper) {
        this.users = users;
        this.mapper = mapper;
    }

    @GetMapping("/me")
    public ApiDtos.UserEnvelope me(HttpServletRequest request) {
        User user = users.get(RequestContext.userId(request));
        return new ApiDtos.UserEnvelope(mapper.user(user, true));
    }

    /**
     * Меняет никнейм (без проверки уникальности), username (с проверкой занятости)
     * и/или описание профиля.
     */
    @PatchMapping("/me")
    public ApiDtos.UserEnvelope updateMe(@RequestBody ApiDtos.UpdateProfileRequest body,
                                         HttpServletRequest request) {
        User user = users.updateProfile(
                RequestContext.userId(request),
                new UserService.UpdateProfileInput(body.nickname(), body.username(), body.bio())
        );
        return new ApiDtos.UserEnvelope(mapper.user(user, true));
    }

    /** Проверка занятости username для формы (можно вызывать с debounce). */
    @GetMapping("/username-available")
    public ApiDtos.UsernameAvailabilityResponse usernameAvailable(
            @RequestParam(required = false) String username) {
        if (username == null || username.isBlank()) {
            throw new ValidationException(Map.of("username", "укажите имя пользователя"));
        }
        UserService.UsernameStatus status = users.checkUsername(username);
        return new ApiDtos.UsernameAvailabilityResponse(
                status.username(),
                status.available(),
                status.reason()
        );
    }

    @PostMapping(value = "/me/avatar", consumes = MediaType.MULTIPART_FORM_DATA_VALUE)
    public ApiDtos.UserEnvelope uploadAvatar(@RequestPart("avatar") MultipartFile avatar,
                                             HttpServletRequest request) {
        User user = users.setAvatar(RequestContext.userId(request), avatar);
        return new ApiDtos.UserEnvelope(mapper.user(user, true));
    }

    @DeleteMapping("/me/avatar")
    public ApiDtos.UserEnvelope deleteAvatar(HttpServletRequest request) {
        User user = users.deleteAvatar(RequestContext.userId(request));
        return new ApiDtos.UserEnvelope(mapper.user(user, true));
    }

    /** Публичный профиль. Email виден только владельцу. */
    @GetMapping("/{id}")
    public ApiDtos.UserEnvelope getById(@PathVariable String id, HttpServletRequest request) {
        UUID targetId;
        try {
            targetId = UUID.fromString(id);
        } catch (IllegalArgumentException e) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_ID",
                    "Некорректный идентификатор пользователя");
        }

        UUID currentUserId = RequestContext.userId(request);
        User user = users.get(targetId);
        return new ApiDtos.UserEnvelope(mapper.user(user, targetId.equals(currentUserId)));
    }
}

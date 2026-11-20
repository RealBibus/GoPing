package com.aerisflow.messenger.error;

import org.springframework.http.HttpStatus;

/**
 * Ошибка API со стабильным кодом. Клиент ориентируется на code, message — для человека.
 */
public class ApiException extends RuntimeException {

    private final HttpStatus status;
    private final String code;
    private final Object details;

    public ApiException(HttpStatus status, String code, String message) {
        this(status, code, message, null);
    }

    public ApiException(HttpStatus status, String code, String message, Object details) {
        super(message);
        this.status = status;
        this.code = code;
        this.details = details;
    }

    public HttpStatus status() {
        return status;
    }

    public String code() {
        return code;
    }

    public Object details() {
        return details;
    }

    public static ApiException unauthorized() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "UNAUTHORIZED", "Требуется авторизация");
    }

    public static ApiException invalidToken() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "UNAUTHORIZED", "Токен недействителен или истёк");
    }

    public static ApiException notFound() {
        return new ApiException(HttpStatus.NOT_FOUND, "NOT_FOUND", "Не найдено");
    }

    public static ApiException usernameTaken() {
        return new ApiException(HttpStatus.CONFLICT, "USERNAME_TAKEN", "Это имя пользователя уже занято",
                java.util.Map.of("username", "уже занято"));
    }

    public static ApiException emailTaken() {
        return new ApiException(HttpStatus.CONFLICT, "EMAIL_TAKEN", "Пользователь с такой почтой уже существует",
                java.util.Map.of("email", "уже используется"));
    }

    public static ApiException invalidCredentials() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_CREDENTIALS", "Неверный логин или пароль");
    }

    public static ApiException invalidRefreshToken() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_REFRESH_TOKEN", "Сессия истекла. Войдите снова.");
    }
}

package com.goping.error;

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

    public static ApiException githubAlreadyLinked() {
        return new ApiException(HttpStatus.CONFLICT, "GITHUB_ALREADY_LINKED",
                "Этот GitHub уже привязан к другому пользователю");
    }

    public static ApiException invalidServerToken() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_SERVER_TOKEN",
                "Серверный токен недействителен, отозван или уже использован");
    }

    public static ApiException invalidRefreshToken() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "INVALID_REFRESH_TOKEN",
                "Сессия истекла. Войдите снова.");
    }

    public static ApiException githubNotConfigured() {
        return new ApiException(HttpStatus.SERVICE_UNAVAILABLE, "GITHUB_NOT_CONFIGURED",
                "Вход через GitHub не настроен на сервере. Используйте серверный токен.");
    }

    public static ApiException githubAuthFailed() {
        return githubAuthFailed("Не удалось подтвердить вход через GitHub");
    }

    public static ApiException githubAuthFailed(String message) {
        return new ApiException(HttpStatus.UNAUTHORIZED, "GITHUB_AUTH_FAILED",
                message == null || message.isBlank() ? "Не удалось подтвердить вход через GitHub" : message);
    }

    public static ApiException githubStateExpired() {
        return new ApiException(HttpStatus.GONE, "GITHUB_STATE_EXPIRED",
                "Вход через GitHub истёк. Начните заново.");
    }

    public static ApiException adminDisabled() {
        return new ApiException(HttpStatus.SERVICE_UNAVAILABLE, "ADMIN_DISABLED",
                "Админ-API выключен: задайте ADMIN_SECRET на сервере");
    }

    public static ApiException adminUnauthorized() {
        return new ApiException(HttpStatus.UNAUTHORIZED, "ADMIN_UNAUTHORIZED",
                "Неверный секрет администратора");
    }
}

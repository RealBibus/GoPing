package com.aerisflow.messenger.service;

import java.util.Locale;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Правила валидации. Те же самые для регистрации и смены профиля.
 */
public final class Validation {

    private static final Pattern USERNAME_PATTERN = Pattern.compile("^[a-z][a-z0-9_]{2,31}$");
    private static final Pattern EMAIL_PATTERN =
            Pattern.compile("^[a-z0-9._%+\\-]+@[a-z0-9.\\-]+\\.[a-z]{2,}$");

    private static final Set<String> RESERVED_USERNAMES = Set.of(
            "admin", "administrator", "aerisflow", "api", "bot", "channel", "everyone",
            "help", "here", "info", "me", "moderator", "news", "null", "official",
            "root", "security", "support", "system", "undefined", "user", "username"
    );

    private Validation() {
    }

    /** Результат проверки поля: либо значение, либо текст ошибки. */
    public record Checked(String value, String error) {
        static Checked ok(String value) {
            return new Checked(value, null);
        }

        static Checked fail(String value, String error) {
            return new Checked(value, error);
        }

        public boolean hasError() {
            return error != null;
        }
    }

    public static String normalizeUsername(String raw) {
        return raw == null ? "" : raw.trim().toLowerCase(Locale.ROOT);
    }

    /**
     * Пустая строка — username корректен;
     * "invalid" — неверный формат; "reserved" — зарезервирован.
     */
    public static String usernameProblem(String raw) {
        String username = normalizeUsername(raw);
        if (username.length() < 3 || username.length() > 32 || !USERNAME_PATTERN.matcher(username).matches()) {
            return "invalid";
        }
        if (RESERVED_USERNAMES.contains(username)) {
            return "reserved";
        }
        return "";
    }

    public static Checked username(String raw) {
        String username = normalizeUsername(raw);
        return switch (usernameProblem(raw)) {
            case "invalid" -> Checked.fail(username,
                    "имя пользователя: 3–32 символа, латинские буквы, цифры и _, первая — буква");
            case "reserved" -> Checked.fail(username, "это имя пользователя зарезервировано");
            default -> Checked.ok(username);
        };
    }

    public static Checked nickname(String raw) {
        String nickname = raw == null ? "" : raw.trim();
        if (nickname.isEmpty()) {
            return Checked.fail(nickname, "укажите никнейм");
        }
        if (nickname.codePointCount(0, nickname.length()) > 50) {
            return Checked.fail(nickname, "никнейм должен быть не длиннее 50 символов");
        }
        for (int i = 0; i < nickname.length(); ) {
            int codePoint = nickname.codePointAt(i);
            if (Character.isISOControl(codePoint)) {
                return Checked.fail(nickname, "никнейм содержит недопустимые символы");
            }
            i += Character.charCount(codePoint);
        }
        return Checked.ok(nickname);
    }

    public static Checked bio(String raw) {
        String bio = raw == null ? "" : raw.trim();
        if (bio.codePointCount(0, bio.length()) > 300) {
            return Checked.fail(bio, "описание должно быть не длиннее 300 символов");
        }
        for (int i = 0; i < bio.length(); ) {
            int codePoint = bio.codePointAt(i);
            if (Character.isISOControl(codePoint) && codePoint != '\n' && codePoint != '\t') {
                return Checked.fail(bio, "описание содержит недопустимые символы");
            }
            i += Character.charCount(codePoint);
        }
        return Checked.ok(bio);
    }

    public static Checked email(String raw) {
        String email = raw == null ? "" : raw.trim().toLowerCase(Locale.ROOT);
        if (email.isEmpty()) {
            return Checked.fail(email, "укажите email");
        }
        if (email.length() > 254) {
            return Checked.fail(email, "email слишком длинный");
        }
        if (!EMAIL_PATTERN.matcher(email).matches()) {
            return Checked.fail(email, "укажите корректный email");
        }
        return Checked.ok(email);
    }

    public static Checked password(String raw) {
        String password = raw == null ? "" : raw;
        if (password.length() < 8) {
            return Checked.fail(password, "пароль должен быть не короче 8 символов");
        }
        if (password.getBytes(java.nio.charset.StandardCharsets.UTF_8).length > 72) {
            return Checked.fail(password, "пароль должен быть не длиннее 72 байт");
        }
        return Checked.ok(password);
    }
}

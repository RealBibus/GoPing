package com.goping.service;

import java.util.Locale;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Правила валидации профиля. Те же самые для GitHub-профиля и смены данных.
 */
public final class Validation {

    private static final Pattern USERNAME_PATTERN = Pattern.compile("^[a-z][a-z0-9_]{2,31}$");

    private static final Set<String> RESERVED_USERNAMES = Set.of(
            "admin", "administrator", "api", "bot", "channel", "everyone",
            "goping", "help", "here", "info", "me", "moderator", "news", "null",
            "official", "ping", "root", "security", "support", "system",
            "undefined", "user", "username"
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

    public static Checked message(String raw) {
        String text = raw == null ? "" : raw.strip();
        if (text.isEmpty()) {
            return Checked.fail(text, "сообщение не может быть пустым");
        }
        if (text.codePointCount(0, text.length()) > 4000) {
            return Checked.fail(text, "сообщение должно быть не длиннее 4000 символов");
        }
        for (int i = 0; i < text.length(); ) {
            int codePoint = text.codePointAt(i);
            if (Character.isISOControl(codePoint) && codePoint != '\n' && codePoint != '\t') {
                return Checked.fail(text, "сообщение содержит недопустимые символы");
            }
            i += Character.charCount(codePoint);
        }
        return Checked.ok(text);
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
}

package com.goping.github;

/**
 * Минимальный клиент GitHub OAuth. Выделен в интерфейс, чтобы тесты
 * могли подменить сетевые вызовы.
 */
public interface GitHubClient {

    record Profile(
            long id,
            String login,
            String name,
            String avatarUrl
    ) {
    }

    Profile authenticate(String code);
}

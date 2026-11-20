# Aerisflow Messenger — сервер

REST API мессенджера: регистрация, вход, профиль, аватарки и заглушка чатов.
Клиент не важен: WPF, браузер или Flutter работают с одним и тем же контрактом.

Стек: **Java 21**, **Spring Boot 3.3**, **SQLite** (файл, без отдельного сервера),
Maven, JWT (HS256), BCrypt.

## Требования

- JDK 21+
- Maven 3.9+ **или** Maven Wrapper (`mvnw.cmd` / `./mvnw`), если он есть в репозитории

## Быстрый старт

```powershell
cd server
copy .env.example .env
# откройте .env и задайте свой JWT_SECRET (не короче 32 символов)

# Windows
.\mvnw.cmd spring-boot:run
# или, если Maven установлен глобально
mvn spring-boot:run
```

Сервер поднимется на `http://localhost:8080`. База и аватарки создаются
автоматически в `./data/`.

Проверка:

```powershell
curl http://localhost:8080/healthz
# {"status":"ok"}

curl http://localhost:8080/readyz
# {"status":"ready"}
```

Сборка запускаемого jar:

```powershell
.\mvnw.cmd -B -DskipTests package
java -jar target\messenger-server-1.0.0.jar
```

Тесты (юнит + сквозные на настоящей SQLite во временной папке):

```powershell
.\mvnw.cmd test
```

## Настройки

Все параметры читаются из переменных окружения; `.env` подхватывается
автоматически (`spring.config.import`). Значения по умолчанию — в
`src/main/resources/application.yml`.

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `HTTP_PORT` | `8080` | Порт HTTP |
| `DATABASE_PATH` | `./data/messenger.db` | Файл SQLite |
| `JWT_SECRET` | — | **Обязателен**, ≥ 32 символов |
| `JWT_ISSUER` | `aerisflow-messenger` | Issuer access-токенов |
| `ACCESS_TOKEN_TTL` | `PT15M` | Время жизни access-токена |
| `REFRESH_TOKEN_TTL` | `P30D` | Время жизни refresh-токена |
| `UPLOAD_DIR` | `./data/uploads` | Корень файлов (аватарки) |
| `MAX_AVATAR_BYTES` | `5242880` | Максимальный размер аватарки (5 МБ) |
| `MAX_UPLOAD_BYTES` | `10485760` | Лимит multipart-запроса (должен быть больше `MAX_AVATAR_BYTES`) |
| `PUBLIC_BASE_URL` | `http://localhost:8080` | Из него строятся абсолютные ссылки на аватарки |
| `CORS_ORIGINS` | `*` | `*` или список origin через запятую |
| `AUTH_RATE_LIMIT` | `30` | Запросов в минуту на `/auth/*` с одного IP |
| `AVAILABILITY_RATE_LIMIT` | `120` | Запросов в минуту на проверку username |
| `AUTO_MIGRATE` | `true` | Применять SQL-миграции при старте |

`JWT_SECRET` короче 32 символов — приложение не стартует (это защита от
случайного дефолтного секрета в проде).

## Формат API

- База: `/api/v1`
- JSON в camelCase, даты — ISO-8601 в UTC (`2026-11-19T16:50:00Z`)
- Авторизация: `Authorization: Bearer <accessToken>`
- Идентификаторы — UUID строками
- Ответ — JSON-объект; списки обёрнуты (`{"items": [...], "nextCursor": null}`)
- Ошибка — всегда конверт:

```json
{
  "error": {
    "code": "USERNAME_TAKEN",
    "message": "Это имя пользователя уже занято",
    "details": { "username": "уже занято" },
    "requestId": "c0ffee00-..."
  }
}
```

`details` есть не всегда. `requestId` совпадает с заголовком `X-Request-ID` —
удобно искать запрос в логах.

### Аутентификация

| Метод | Путь | Тело | Ответ |
|---|---|---|---|
| `POST` | `/api/v1/auth/register` | `{email, username, password, nickname?}` | `201` + `AuthResponse` |
| `POST` | `/api/v1/auth/login` | `{login, password}` | `200` + `AuthResponse` |
| `POST` | `/api/v1/auth/refresh` | `{refreshToken}` | `200` + `AuthResponse` |
| `POST` | `/api/v1/auth/logout` | `{refreshToken}` | `204` |

`login` — это email **или** username, регистр не важен.
`nickname` при регистрации необязателен: если пуст, берётся username.

`AuthResponse`:

```json
{
  "user": { "...UserDto..." },
  "tokenType": "Bearer",
  "accessToken": "eyJ...",
  "refreshToken": "VAqdu1ti...",
  "expiresIn": 900,
  "expiresAt": "2026-11-19T17:05:02Z"
}
```

Refresh-токен одноразовый: каждый `/refresh` выдаёт новую пару.
Повторное использование уже отозванного токена считается кражей и отзывает
**все** сессии пользователя.

### Профиль

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1/users/me` | Bearer | Свой профиль (с email) |
| `PATCH` | `/api/v1/users/me` | Bearer | `{nickname?, username?, bio?}` — частичное обновление |
| `GET` | `/api/v1/users/username-available?username=...` | публично | Проверка занятости для формы |
| `POST` | `/api/v1/users/me/avatar` | Bearer | `multipart/form-data`, поле `avatar` |
| `DELETE` | `/api/v1/users/me/avatar` | Bearer | Удалить аватарку |
| `GET` | `/api/v1/users/{id}` | Bearer | Публичный профиль (без email) |

Правила:

- **nickname** — отображаемое имя, **не уникален**; 1–50 символов.
- **username** — уникален, 3–32 символа: латинские буквы, цифры и `_`,
  первая — буква. Хранится в нижнем регистре. Есть список зарезервированных
  (`admin`, `api`, `support`, ...). При смене занятость проверяется на сервере.
- **bio** — до 300 символов, переносы строк разрешены.
- **avatar** — JPEG, PNG или GIF, не больше 4096×4096 и 5 МБ.
  При замене старый файл удаляется. В ответе `avatarUrl` — абсолютная ссылка.
- Аватарки отдаются как статика: `GET /media/avatars/<uuid>.<ext>`.

`UserDto`:

```json
{
  "id": "8f14e45f-ceea-467f-a1b1-2a2c0a2c0a2c",
  "username": "alice",
  "nickname": "Алиса",
  "email": "alice@example.com",
  "bio": "",
  "avatarUrl": "http://localhost:8080/media/avatars/....png",
  "createdAt": "2026-11-19T16:50:00Z",
  "updatedAt": "2026-11-19T16:50:00Z"
}
```

`email` присутствует только в своём профиле (`/me`). В чужом профиле поля нет.

`GET /api/v1/users/username-available`:

```json
{ "username": "alice", "available": false, "reason": "taken" }
```

`reason`: `taken`, `invalid` или `reserved`; при свободном имени — `null`.
Эндпоинт публичный, чтобы форму можно было проверять до входа.

### Чаты

| Метод | Путь | Ответ |
|---|---|---|
| `GET` | `/api/v1/chats?limit=30` | `{"items": [], "nextCursor": null}` |

Чатов пока нет: клиент получает пустой список и показывает заглушку.
Формат ответа финальный — когда появятся данные, контракт не изменится.

## Коды ошибок

| HTTP | `code` | Когда |
|---|---|---|
| `400` | `INVALID_JSON` | Тело запроса — не JSON |
| `400` | `INVALID_ID` | Некорректный UUID в пути |
| `401` | `UNAUTHORIZED` | Нет/просрочен/битый access-токен |
| `401` | `INVALID_CREDENTIALS` | Неверный логин или пароль |
| `401` | `INVALID_REFRESH_TOKEN` | Refresh-токен неизвестен, отозван или истёк |
| `404` | `NOT_FOUND` | Нет пользователя/маршрута |
| `405` | `METHOD_NOT_ALLOWED` | Метод не поддерживается |
| `409` | `USERNAME_TAKEN` | username занят |
| `409` | `EMAIL_TAKEN` | email уже используется |
| `413` | `AVATAR_TOO_LARGE` | Файл больше лимита |
| `415` | `UNSUPPORTED_MEDIA_TYPE` | Не JPEG/PNG/GIF или битое изображение |
| `422` | `VALIDATION_ERROR` | Ошибки полей в `details` |
| `429` | `RATE_LIMITED` | Слишком много запросов (заголовок `Retry-After`) |
| `500` | `INTERNAL` | Внутренняя ошибка |

## Структура

```
server/
├── pom.xml
├── .env.example
└── src/
    ├── main/
    │   ├── java/com/aerisflow/messenger/
    │   │   ├── config/      настройки, SQLite, CORS, статика
    │   │   ├── db/          мигратор и трансляция ошибок SQLite
    │   │   ├── domain/      User, Session, Chat
    │   │   ├── error/       ApiException, ValidationException
    │   │   ├── media/       сохранение и проверка аватарок
    │   │   ├── repository/  SQL-запросы (Spring JDBC)
    │   │   ├── security/    JWT и контекст запроса
    │   │   ├── service/     бизнес-логика и валидация
    │   │   └── web/         контроллеры, DTO, фильтры
    │   └── resources/
    │       ├── application.yml
    │       └── db/migration/V1__init.sql
    └── test/java/...        юнит- и сквозные тесты
```

Миграции — обычные `.sql` из `src/main/resources/db/migration`, применяются по
порядку имени; применённые версии хранятся в таблице `schema_migrations`.
Новую миграцию добавляйте как `V2__описание.sql`.

## Особенности SQLite

- WAL, `foreign_keys=ON`, `busy_timeout=5000` — включаются при подключении.
- Время хранится как epoch millis (`INTEGER`), UUID — как `TEXT`.
- Записи SQLite сериализует, поэтому пул потоков небольшой (5 соединений).
- Уникальные ограничения SQLite переводятся в обычные Spring-исключения
  (`SqliteExceptionTranslator`), чтобы клиент получал `409`, а не `500`.

## Безопасность

- Пароли — BCrypt (cost 12), в базе только хеш.
- Refresh-токены хранятся как SHA-256; сырой токен есть только у клиента.
- Access-токен живёт 15 минут, refresh — 30 дней.
- Rate limit в памяти на процесс (без Redis); для нескольких инстансов
  понадобится общий счётчик.
- Не коммитьте `.env` и папку `data/` — они в `.gitignore`.

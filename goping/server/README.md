# GoPing — сервер

**GoPing — a local messenger for you and your friends, with login via GitHub or a server token!**

REST API мессенджера: вход через GitHub, вход по одноразовому серверному
токену, профиль, аватарки и заглушка чатов. Клиент не важен: WPF, браузер или
мобильное приложение работают с одним и тем же контрактом.

Стек: **Java 21**, **Spring Boot 3.3**, **SQLite** (файл, без отдельного сервера),
Maven, JWT (HS256). Паролей и классической регистрации нет.

## Требования

- JDK 21+
- Maven 3.9+ **или** Maven Wrapper (`mvnw.cmd` / `./mvnw`), если он есть в репозитории

## Быстрый старт

```powershell
cd server
copy .env.example .env
# откройте .env и задайте:
#   JWT_SECRET   — не короче 32 символов;
#   ADMIN_SECRET — чтобы можно было выдавать серверные токены;
#   GITHUB_*     — если нужен вход через GitHub (необязательно).

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
java -jar target\goping-server-1.0.0.jar
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
| `DATABASE_PATH` | `./data/goping.db` | Файл SQLite |
| `JWT_SECRET` | — | **Обязателен**, ≥ 32 символов |
| `JWT_ISSUER` | `goping` | Issuer access-токенов |
| `ACCESS_TOKEN_TTL` | `PT15M` | Время жизни access-токена |
| `REFRESH_TOKEN_TTL` | `P30D` | Время жизни refresh-токена |
| `ADMIN_SECRET` | — | Секрет админ-API. Пусто — выдача токенов выключена |
| `GITHUB_CLIENT_ID` | — | Client ID GitHub OAuth App |
| `GITHUB_CLIENT_SECRET` | — | Client Secret GitHub OAuth App |
| `GITHUB_CALLBACK_URL` | `http://localhost:8080/api/v1/auth/github/callback` | Callback, указанный в GitHub |
| `GITHUB_SCOPE` | `read:user` | Запрашиваемые права |
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

Если `GITHUB_CLIENT_ID`/`GITHUB_CLIENT_SECRET` пусты, сервер продолжает
работать, но `/auth/github/*` отвечает `503 GITHUB_NOT_CONFIGURED`, а вход
по серверному токену остаётся доступным.

## Серверные токены

Токен выглядит так: `GOPING-Ab3_x-9Qw2Z0` — префикс `GOPING-` и 12 символов
из `A-Z a-z 0-9 _ -`. Случайная часть генерируется `SecureRandom`
(≈ 2⁷² вариантов), поэтому угадать её практически невозможно.

Жизненный цикл:

1. Администратор создаёт токен через админ-API. Сырое значение показывается
   **один раз**; в базе хранится только SHA-256.
2. Пользователь вводит токен в клиенте. Первый успешный вход:
   - атомарно привязывает токен к новому пользователю
     (`ping_xxxxxxxx`, никнейм `Ping XXXX`);
   - выдаёт обычную пару access/refresh.
3. Все последующие входы с этим токеном открывают **того же** пользователя.
4. Токен помнит клиент (в WPF — DPAPI-зашифрованный файл), поэтому повторно
   вводить его не нужно.
5. Отозванный администратором токен перестаёт работать сразу.

Выдать токен:

```powershell
$body = @{ label = "for Alice" } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET } `
  -ContentType "application/json" -Body $body
# {"token":{...},"value":"GOPING-Ab3_x-9Qw2Z0"}
```

Список и отзыв:

```powershell
Invoke-RestMethod http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET }

Invoke-RestMethod -Method Delete `
  -Uri http://localhost:8080/api/v1/admin/tokens/<id> `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET }
```

`X-Admin-Secret` — отдельный секрет, не пользовательская сессия. Он никогда
не выдаётся клиенту.

## GitHub OAuth

1. Создайте OAuth App: GitHub → Settings → Developer settings → OAuth Apps.
2. `Homepage URL`: `http://localhost:8080` (или ваш адрес).
3. `Authorization callback URL`:
   `http://localhost:8080/api/v1/auth/github/callback`.
4. Пропишите `GITHUB_CLIENT_ID` и `GITHUB_CLIENT_SECRET` в `.env`.

Поток входа:

1. Клиент: `GET /api/v1/auth/github/start` → `{authorizeUrl, state, expiresAt}`.
2. Клиент открывает `authorizeUrl` в браузере.
3. GitHub возвращает браузер на `/auth/github/callback`; сервер обменивает
   `code` на профиль и показывает простую HTML-страницу «вернитесь в GoPing».
4. Клиент опрашивает `GET /api/v1/auth/github/poll?state=...`, пока не получит
   `status: "ready"` и `AuthResponse`.

При первом входе через GitHub сервер создаёт пользователя: `username` из
GitHub-логина (при необходимости с суффиксом), `nickname` из имени, аватар —
из GitHub. Никнейм и аватар затем можно менять в клиенте: повторные входы
профиль не перезаписывают.

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

| Метод | Путь | Тело / параметры | Ответ |
|---|---|---|---|
| `POST` | `/api/v1/auth/token` | `{token}` | `200` + `AuthResponse` |
| `POST` | `/api/v1/auth/refresh` | `{refreshToken}` | `200` + `AuthResponse` |
| `POST` | `/api/v1/auth/logout` | `{refreshToken}` | `204` |
| `GET` | `/api/v1/auth/github/start` | — | `{authorizeUrl, state, expiresAt}` |
| `GET` | `/api/v1/auth/github/callback` | `?code=&state=` | HTML-страница |
| `GET` | `/api/v1/auth/github/poll` | `?state=` | `{status, auth}` |

`POST /auth/token` — единственный способ входа по токену. Первый вызов
активирует токен и создаёт пользователя, последующие входят в него же.

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

`GithubPollResponse`:

```json
{ "status": "pending", "auth": null }
{ "status": "ready", "auth": { "...AuthResponse..." } }
```

Refresh-токен одноразовый: каждый `/refresh` выдаёт новую пару.
Повторное использование уже отозванного токена считается кражей и отзывает
**все** сессии пользователя.

### Профиль

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1/users/me` | Bearer | Свой профиль |
| `PATCH` | `/api/v1/users/me` | Bearer | `{nickname?, username?, bio?}` — частичное обновление |
| `GET` | `/api/v1/users/username-available?username=...` | публично | Проверка занятости для формы |
| `POST` | `/api/v1/users/me/avatar` | Bearer | `multipart/form-data`, поле `avatar` |
| `DELETE` | `/api/v1/users/me/avatar` | Bearer | Удалить аватарку |
| `GET` | `/api/v1/users/{id}` | Bearer | Публичный профиль |

Правила:

- **nickname** — отображаемое имя, **не уникален**; 1–50 символов.
- **username** — уникален, 3–32 символа: латинские буквы, цифры и `_`,
  первая — буква. Хранится в нижнем регистре. Есть список зарезервированных
  (`admin`, `api`, `support`, `goping`, `ping`, ...). При смене занятость
  проверяется на сервере.
- **bio** — до 300 символов, переносы строк разрешены.
- **avatar** — JPEG, PNG или GIF, не больше 4096×4096 и 5 МБ.
  При замене старый файл удаляется. В ответе `avatarUrl` — абсолютная ссылка.
  Для GitHub-пользователей `avatarUrl` может указывать на GitHub.
- Аватарки отдаются как статика: `GET /media/avatars/<uuid>.<ext>`.

`UserDto`:

```json
{
  "id": "8f14e45f-ceea-467f-a1b1-2a2c0a2c0a2c",
  "username": "alice",
  "nickname": "Алиса",
  "bio": "",
  "avatarUrl": "http://localhost:8080/media/avatars/....png",
  "createdAt": "2026-11-19T16:50:00Z",
  "updatedAt": "2026-11-19T16:50:00Z"
}
```

Email в API больше не отдаётся: его просто нет в модели.

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
| `401` | `INVALID_SERVER_TOKEN` | Серверный токен неизвестен, отозван или неверного формата |
| `401` | `INVALID_REFRESH_TOKEN` | Refresh-токен неизвестен, отозван или истёк |
| `401` | `GITHUB_AUTH_FAILED` | GitHub не подтвердил вход |
| `401` | `ADMIN_UNAUTHORIZED` | Неверный `X-Admin-Secret` |
| `404` | `NOT_FOUND` | Нет пользователя/маршрута |
| `405` | `METHOD_NOT_ALLOWED` | Метод не поддерживается |
| `409` | `USERNAME_TAKEN` | username занят |
| `409` | `GITHUB_ALREADY_LINKED` | Этот GitHub уже привязан к другому пользователю |
| `410` | `GITHUB_STATE_EXPIRED` | Вход через GitHub истёк — начните заново |
| `413` | `AVATAR_TOO_LARGE` | Файл больше лимита |
| `415` | `UNSUPPORTED_MEDIA_TYPE` | Не JPEG/PNG/GIF или битое изображение |
| `422` | `VALIDATION_ERROR` | Ошибки полей в `details` |
| `429` | `RATE_LIMITED` | Слишком много запросов (заголовок `Retry-After`) |
| `500` | `INTERNAL` | Внутренняя ошибка |
| `503` | `GITHUB_NOT_CONFIGURED` | GitHub OAuth не настроен на сервере |
| `503` | `ADMIN_DISABLED` | `ADMIN_SECRET` не задан |

## Структура

```
server/
├── pom.xml
├── .env.example
└── src/
    ├── main/
    │   ├── java/com/goping/
    │   │   ├── config/      настройки, SQLite, CORS, статика
    │   │   ├── db/          мигратор и трансляция ошибок SQLite
    │   │   ├── domain/      User, Session, Chat
    │   │   ├── error/       ApiException, ValidationException
    │   │   ├── github/      OAuth-клиент и сценарий входа
    │   │   ├── media/       сохранение и проверка аватарок
    │   │   ├── repository/  SQL-запросы (Spring JDBC)
    │   │   ├── security/    JWT и контекст запроса
    │   │   ├── service/     бизнес-логика и валидация
    │   │   └── web/         контроллеры, DTO, фильтры
    │   └── resources/
    │       ├── application.yml
    │       └── db/migration/V1__init.sql, V2__goping_auth.sql
    └── test/java/...        юнит- и сквозные тесты
```

Миграции — обычные `.sql` из `src/main/resources/db/migration`, применяются по
порядку имени; применённые версии хранятся в таблице `schema_migrations`.
Новую миграцию добавляйте как `V3__описание.sql`.

`V2__goping_auth.sql` добавляет `github_id`, `avatar_url` и таблицу
`server_tokens`. Колонки `email` и `password_hash` остаются в схеме только
ради совместимости со старыми базами; новые пользователи получают
синтетические значения, а API их не показывает.

## Особенности SQLite

- WAL, `foreign_keys=ON`, `busy_timeout=5000` — включаются при подключении.
- Время хранится как epoch millis (`INTEGER`), UUID — как `TEXT`.
- Записи SQLite сериализует, поэтому пул потоков небольшой (5 соединений).
- Уникальные ограничения SQLite переводятся в обычные Spring-исключения
  (`SqliteExceptionTranslator`), чтобы клиент получал `409`, а не `500`.

## Безопасность

- Паролей нет: вход только через GitHub или серверный токен.
- Серверные токены и refresh-токены хранятся как SHA-256; сырые значения
  есть только у владельца.
- Привязка серверного токена к пользователю атомарна: два одновременных
  первых входа не создадут двух пользователей.
- Access-токен живёт 15 минут, refresh — 30 дней.
- `ADMIN_SECRET` сравнивается за постоянное время; пустое значение выключает
  админ-API целиком.
- Rate limit в памяти на процесс (без Redis); для нескольких инстансов
  понадобится общий счётчик.
- Не коммитьте `.env` и папку `data/` — они в `.gitignore`.

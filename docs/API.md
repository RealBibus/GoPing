# GoPing API — документация для форков

Полный контракт сервера GoPing: REST, WebSocket, шифрование, конфигурация.
Клиент не важен — WPF, браузер или мобильное приложение работают с одним и тем же API.

- Базовый URL: `http://<host>:8080` (порт настраивается).
- Все ID — UUID-строки. Все времена — ISO-8601 (`2026-05-23T12:00:00Z`).
- Аутентификация: заголовок `Authorization: Bearer <accessToken>` для всех
  `/api/v1/**`, кроме перечисленных ниже публичных путей.
- Ошибки — единый конверт:

```json
{ "error": { "code": "VALIDATION_ERROR", "message": "…", "details": { "text": "…" }, "requestId": "…" } }
```

Коды: `UNAUTHORIZED`, `INVALID_SERVER_TOKEN`, `INVALID_REFRESH_TOKEN`, `NOT_FOUND`,
`INVALID_ID`, `VALIDATION_ERROR`, `USERNAME_TAKEN` (409), `FORBIDDEN`,
`DELETE_OTHERS_DISABLED` (403), `INVALID_PIN_SCOPE`, `MESSAGE_DAMAGED`,
`AVATAR_TOO_LARGE` / `VOICE_TOO_LARGE` (413), `UNSUPPORTED_MEDIA_TYPE` (415),
`INVALID_VOICE`, `RATE_LIMITED` (429, заголовок `Retry-After: 60`),
`ADMIN_DISABLED` (503), `ADMIN_UNAUTHORIZED`, `GITHUB_NOT_CONFIGURED` (503),
`GITHUB_AUTH_FAILED` (401), `GITHUB_STATE_EXPIRED` (410), `GITHUB_ALREADY_LINKED` (409),
`DB_UNAVAILABLE` (503). Каждый ответ содержит `X-Request-ID`.

---

## 1. Аутентификация

Паролей и регистрации нет. Вход — по серверному токену или через GitHub OAuth.

### POST `/api/v1/auth/token` (публичный)

```json
{ "token": "GOPING-XXXXXXXXXXXX" }
```

Токен выдаёт админ (§7). Первый вход **активирует** токен: создаётся пользователь
`ping_xxxxxxxx` / `Ping XXXX`, токен навсегда привязывается к нему. Повторные
входы с тем же токеном попадают в того же пользователя.

### POST `/api/v1/auth/refresh` (публичный)

```json
{ "refreshToken": "…" }
```

**Ротация**: каждый refresh выдаёт новую пару токенов, старый refresh гасится.
Повторное использование погашенного refresh-токена гасит **все** сессии
пользователя (защита от кражи).

### POST `/api/v1/auth/logout` (публичный)

```json
{ "refreshToken": "…" }
```
→ `204`. Гасит сессию.

### Ответ всех успешных входов — `AuthResponse`

```json
{
  "user": { "id": "…", "username": "…", "nickname": "…", "bio": "…",
            "avatarUrl": "…", "createdAt": "…", "updatedAt": "…",
            "online": true, "lastSeenAt": "…" },
  "tokenType": "Bearer",
  "accessToken": "<JWT>",
  "refreshToken": "<opaque>",
  "expiresIn": 900,
  "expiresAt": "2026-05-23T12:15:00Z"
}
```

- **Access token** — JWT HS256 (`JWT_SECRET`), claims: `sub` = userId,
  `token_use: "access"`, `iss`, `iat`/`nbf`/`exp`, `jti`. TTL по умолчанию
  **15 минут** (`ACCESS_TOKEN_TTL`). Тот же токен используется для WebSocket.
- **Refresh token** — случайные 32 байта (Base64URL), на сервере хранится
  SHA-256-хеш. TTL по умолчанию **30 дней** (`REFRESH_TOKEN_TTL`).

### GitHub OAuth (poll-флоу, клиент не видит code)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/v1/auth/github/start` | → `{"authorizeUrl", "state", "expiresAt"}`. State живёт 10 минут. 503 `GITHUB_NOT_CONFIGURED`, если OAuth не настроен. |
| GET | `/api/v1/auth/github/callback?code&state` | Сюда редиректит GitHub (браузер). Сервер меняет code на токен, тянет профиль, отдаёт HTML-страницу успеха/ошибки. Токены здесь не выдаются. |
| GET | `/api/v1/auth/github/poll?state=…` | Клиент опрашивает: `{"status":"pending","auth":null}` → `{"status":"ready","auth":AuthResponse}`. State потребляется один раз. 410 — истёк, 401 — отказ/сбой. |

Первый вход создаёт пользователя: username из GitHub-логина (sanitized,
уникализированный), nickname и аватар — из профиля GitHub. Аккаунт матчится
по числовому `github_id`.

### Rate limits

Per-IP, окно 60 с: auth/admin пути — 30 запросов/мин (`AUTH_RATE_LIMIT`),
`username-available` — 120/мин (`AVAILABILITY_RATE_LIMIT`). Превышение → 429.

---

## 2. Пользователи

**UserDto**: `id, username, nickname, bio, avatarUrl, createdAt, updatedAt, online, lastSeenAt`.

| Метод | Путь | Тело / параметры | Ответ |
|---|---|---|---|
| GET | `/api/v1/users/me` | — | `{"user": UserDto}` |
| PATCH | `/api/v1/users/me` | `{"nickname"?, "username"?, "bio"?}` | `{"user": UserDto}` |
| GET | `/api/v1/users/search?q=…` | подстрока username, без учёта регистра | `{"users": [UserDto]}` (≤20, без себя) |
| GET | `/api/v1/users/username-available?username=…` | **публичный** | `{"username", "available", "reason": "invalid"\|"reserved"\|"taken"\|null}` |
| POST | `/api/v1/users/me/avatar` | multipart, часть **`avatar`** | `{"user": UserDto}` |
| DELETE | `/api/v1/users/me/avatar` | — | `{"user": UserDto}` |
| GET | `/api/v1/users/{id}` | UUID | `{"user": UserDto}` |
| GET | `/api/v1/users/username/{username}` | — | `{"user": UserDto}` |

Ограничения: nickname ≤50 символов (не уникален); username —
`^[a-z][a-z0-9_]{2,31}$`, уникален, есть зарезервированные (admin, root, goping, …);
bio ≤300. Аватар: JPEG/PNG/GIF, ≤4096×4096, ≤5 МиБ (`MAX_AVATAR_BYTES`).

Списка друзей нет — личный чат создаётся сразу по username (§3).

---

## 3. Чаты и сообщения

**ChatDto**: `id, kind ("direct"|"favorites"), title, username, avatarUrl,
createdAt, peerUserId, lastMessageText, lastMessageAt, peerOnline,
peerLastSeenAt, peerTyping`.

**MessageDto**: `id, chatId, sender {id, username, nickname, avatarUrl}, text,
algorithm, kind ("text"|"voice"|"call"), createdAt,
replyTo {id, text, senderNickname}|null, status ("sent"|"read"),
pinScope ("me"|"chat"|null), editedAt, forwardedFromSenderNickname, forwardedFromText`.

`status` относителен смотрящего: `"read"` только у своих сообщений после
прочтения собеседником. `nextCursor` в списках пока всегда `null`
(окно по limit, без курсорной пагинации).

| Метод | Путь | Тело / параметры | Ответ |
|---|---|---|---|
| GET | `/api/v1/chats?limit=30` | limit 1–100 | `{"items": [ChatDto], "nextCursor": null}` |
| POST | `/api/v1/chats` | `{"username": "peer"}` | 201 ChatDto (идемпотентно; шлёт `chat.updated`) |
| POST | `/api/v1/chats/{chatId}/typing` | — | 204 (TTL 5 с; WS `typing`) |
| POST | `/api/v1/chats/{chatId}/read` | — | 204 (WS `messages.read`) |
| GET | `/api/v1/chats/{chatId}/messages?limit=50` | limit 1–100 | `{"items": [MessageDto], …}` — новейшее окно |
| GET | `/api/v1/chats/{chatId}/pins` | — | `{"items": [MessageDto], …}` — все видимые пины |
| POST | `/api/v1/chats/{chatId}/messages` | `{"text", "replyToId"?}` | 201 MessageDto |
| POST | `/api/v1/chats/{chatId}/messages/voice` | multipart (§5) | 201 MessageDto |
| PATCH | `/api/v1/chats/{chatId}/messages/{messageId}` | `{"text"}` | 200 MessageDto (только свои **текстовые**; ставит `editedAt`) |
| POST | `/api/v1/chats/{chatId}/messages/forward` | `{"fromChatId", "messageId"}` | 201 MessageDto в целевом чате |
| DELETE | `/api/v1/chats/{chatId}/messages/{messageId}` | `{"forEveryone": bool}` (default false) | 204 |
| POST | `/api/v1/chats/{chatId}/messages/{messageId}/pin` | `{"scope": "me"\|"chat"}` | 204 |
| DELETE | `/api/v1/chats/{chatId}/messages/{messageId}/pin` | `{"scope": "me"\|"chat"}` | 204 |

Нюансы:

- Текст: 1–4000 кодпоинтов; reply — только внутри того же чата.
- `GET /chats` заодно шлёт presence-heartbeat и автосоздаёт чат **Favorites**
  (self-chat для заметок и пересылки себе).
- Удаление: `forEveryone=true` — жёсткое удаление (чужие можно, если
  `ALLOW_DELETE_OTHERS_MESSAGES=true`, иначе 403 `DELETE_OTHERS_DISABLED`);
  `false` — скрытие только для себя.
- Пересылка: сервер расшифровывает оригинал и **заново шифрует** в целевой чат
  (никакого копирования ciphertext). Повреждённое сообщение → 400 `MESSAGE_DAMAGED`.
  Ставятся `forwardedFromSenderNickname` и `forwardedFromText` (превью ≤300 символов).
- Пин `scope=me` виден только себе, `scope=chat` — всем в чате.
- **Звонки в чате** — не отдельный REST-ресурс: сервер сам пишет сообщения
  `kind:"call"`, их `text` — JSON `{"status":"ended"|"missed"|"declined"|"cancelled","durationSec":N}`.
  Приходят как обычные `message.created`.

---

## 4. Presence, конфиг, RTC

| Метод | Путь | Ответ |
|---|---|---|
| POST | `/api/v1/presence/heartbeat` | 204. Онлайн = активный WS или heartbeat свежее 25 с. |
| GET | `/api/v1/client-config` | `{"allowDeleteOthersMessages": bool}` |
| GET | `/api/v1/rtc/config` | `{"iceServers": [{"urls": ["stun:…"]}, {"urls": ["turn:…"], "username", "credential"}]}` — TURN-запись только если задан `TURN_URLS`. |
| GET | `/healthz` (публичный, без префикса) | `{"status":"ok"}` |
| GET | `/readyz` (публичный) | `{"status":"ready"}` или 503 `DB_UNAVAILABLE` |

### Медиа (публично, без auth)

`GET /media/**` раздаётся из `UPLOAD_DIR` с `Cache-Control: public, max-age=86400`:
- `/media/avatars/<uuid>.<jpg|png|gif>`
- `/media/voice/<uuid>.<ext>`

`avatarUrl` в ответах уже абсолютный (префикс `PUBLIC_BASE_URL`; у GitHub-входа —
URL аватара GitHub).

---

## 5. Голосовые сообщения

### POST `/api/v1/chats/{chatId}/messages/voice` — `multipart/form-data`

- часть **`audio`** — файл;
- **`durationSec`** (int, опционально, 0–3600);
- **`replyToId`** (UUID, опционально).

Валидация: непустой, ≤10 МиБ (`VOICE_TOO_LARGE` 413); формат —
**webm, ogg, mp3, wav, m4a, mp4** (mp4/m4a хранятся как `.m4a`), иначе 415.
Файл сохраняется в `UPLOAD_DIR/voice/<uuid>.<ext>`.

Хранимое сообщение: `kind:"voice"`, зашифрованный `text` =
`{"path":"/media/voice/<uuid>.<ext>","durationSec":N}`. В API/WS ответах `path`
уже заменён на абсолютный URL (`PUBLIC_BASE_URL + /media/voice/…`) — клиенту
достаточно скачать его и проиграть.

---

## 6. WebSocket `/api/v1/ws`

**Подключение**: `ws://host:8080/api/v1/ws?token=<accessToken>` — JWT
проверяется на handshake, отдельного сообщения-логина нет. При коннекте:
presence-heartbeat, броадкаст `presence` собеседникам, доставка отложенных
call-invite. При закрытии последнего сокета — offline + `presence`.
Поддерживается несколько сессий одного пользователя.

### Клиент → сервер (JSON-фреймы)

| `type` | Поля | Эффект |
|---|---|---|
| `ping` | — | ответ `{"type":"pong"}` |
| `typing` | `chatId` | броадкаст `typing` собеседникам |
| `read` | `chatId` | отметка прочтения, броадкаст `messages.read` |
| `call.invite` | `chatId`, `callId` (UUID, генерит клиент) | создаёт звонок в памяти, шлёт `call.invite` пиру |
| `call.accept` | `callId` | пир получает `call.accepted` |
| `call.reject` | `callId` | пир получает `call.rejected`; в чат пишется «declined» |
| `call.end` | `callId`, `reason?` (default `"hangup"`) | пир получает `call.ended`; в чат «ended» (если был accept) или «missed» |
| `call.signal` | `callId`, `kind` (`"offer"`/`"answer"`/`"ice"`), `data` (SDP/ICE, произвольный JSON) | relay пиру как есть |
| `call.rejoin` | `callId` | после реконнекта WS → пир получает `call.peer-rejoined` (нужен ICE-restart) |

Неизвестные типы молча игнорируются.

### Сервер → клиент

| `type` | Поля | Кому |
|---|---|---|
| `pong` | — | запросившему |
| `message.created` | `chatId`, `message` (MessageDto) | участникам чата (+ `chat.updated` каждому) |
| `message.edited` | `chatId`, `message` | участникам (+ `chat.updated`) |
| `message.deleted` | `chatId`, `messageId`, `forEveryone` | чату, если forEveryone; иначе только удалившему (+ `chat.updated`) |
| `message.pinned` | `chatId`, `messageId`, `scope`, `actorId`, `pinned` (bool) | только actor при scope=me, иначе чату |
| `chat.updated` | `chat` (ChatDto, персонализированный) | каждому участнику; также при создании чата |
| `typing` | `chatId`, `userId` | участникам, кроме печатающего |
| `messages.read` | `chatId`, `readerId`, `readAt`, `upToMessageId?` | участникам |
| `presence` | `userId`, `online`, `lastSeenAt?` | всем собеседникам пользователя |
| `call.invite` | `callId`, `chatId`, `fromUserId`, `toUserId` | вызываемому (повторно доставляется при реконнекте, если звонку ≤10 мин) |
| `call.accepted` / `call.rejected` | `callId`, `chatId`, `fromUserId`, `toUserId` | пиру |
| `call.ended` | `callId`, `chatId?`, `fromUserId?`, `toUserId?`, `reason` (`"hangup"` / `"timeout"` / `"server-restart"` / кастом) | пиру; обоим при TTL 10 мин |
| `call.signal` | `callId`, `chatId`, `fromUserId`, `toUserId`, `kind`, `data` | пиру |
| `call.peer-rejoined` | `callId`, `chatId`, `fromUserId`, `toUserId` | пиру |

Состояние звонков — **только в памяти** (TTL 10 мин). Рестарт сервера →
`call.ended reason:"server-restart"`. Медиа (аудио/видео/экран) идёт **P2P
через WebRTC**, сервер его не видит.

### Пример: звонок

1. A: `{"type":"call.invite","chatId":"…","callId":"<uuid>"}`
2. B получает `call.invite` → шлёт `{"type":"call.accept","callId":"…"}`
3. A получает `call.accepted` → создаёт RTCPeerConnection, шлёт
   `{"type":"call.signal","callId":"…","kind":"offer","data":{sdp…}}`
4. B отвечает `kind:"answer"`, обе стороны обмениваются `kind:"ice"`.
5. Завершение: `{"type":"call.end","callId":"…","reason":"hangup"}`.

---

## 7. Админ: выдача серверных токенов

Заголовок **`X-Admin-Secret`** = `ADMIN_SECRET` (не Bearer!). Пустой
`ADMIN_SECRET` → 503 `ADMIN_DISABLED`.

| Метод | Путь | Тело | Ответ |
|---|---|---|---|
| POST | `/api/v1/admin/tokens` | `{"label"?}` (≤80 симв.) | 201 `{"token": AdminTokenDto, "value": "GOPING-…"}` — сырое значение **один раз** |
| GET | `/api/v1/admin/tokens` | — | `{"items": [AdminTokenDto]}` |
| DELETE | `/api/v1/admin/tokens/{id}` | — | 204 (отзыв) |

**AdminTokenDto**: `id, prefix ("GOPING-"+первые 4), label, userId (null до
активации), createdAt, activatedAt, lastUsedAt, revokedAt`.

Пример выдачи токена другу:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = "<твой ADMIN_SECRET>" } `
  -ContentType "application/json" -Body '{"label":"friend"}'
```

---

## 8. Шифрование сообщений

Это **шифрование at rest**, не E2E: клиент шлёт plaintext по TLS, сервер
шифрует перед записью в SQLite и расшифровывает при чтении для участников чата.

- Алгоритм: **ГОСТ Р 34.12-2015 «Кузнечик», режим CTR** (ГОСТ Р 34.13-2015),
  encrypt-then-MAC с **HMAC-SHA256** (32-байтный тег над nonce‖ciphertext),
  случайный nonce на каждое сообщение.
- В БД: Base64 `ciphertext`, `nonce`, `mac` + строка алгоритма
  `"GOST34.12-2015/KUZNYECHIK/CTR"` (видна в `MessageDto.algorithm`).
  Voice/call-payload — такие же зашифрованные JSON-строки.
- Ключи: SHA-256(label‖0x00‖material), labels `goping/message-encryption/v1`
  и `goping/message-authentication/v1`. Material — `MESSAGE_KEY` (64 hex или
  Base64 от 32 байт), иначе байты `JWT_SECRET`.
  **Смена `JWT_SECRET` без заданного `MESSAGE_KEY` делает старые сообщения
  нечитаемыми.** Для форка задай `MESSAGE_KEY` отдельно сразу.
- MAC не сошёлся → текст заменяется на `[сообщение повреждено: не удалось
  проверить подпись]`; пересылка такого → 400 `MESSAGE_DAMAGED`.

---

## 9. Конфигурация сервера

Переменные окружения (без префикса) или файл `.env` в рабочей папке
(`spring.config.import: optional:file:.env`).

| Переменная | Default | Назначение |
|---|---|---|
| `HTTP_PORT` | `8080` | порт |
| `DATABASE_PATH` | `./data/goping.db` | SQLite-файл, авто-миграции (`AUTO_MIGRATE=true`, V1–V8) |
| `JWT_SECRET` | — | **обязателен, ≥32 символов**, иначе сервер не стартует |
| `JWT_ISSUER` | `goping` | iss в JWT |
| `ACCESS_TOKEN_TTL` | `PT15M` | ISO-8601 duration |
| `REFRESH_TOKEN_TTL` | `P30D` | должен быть больше access TTL |
| `UPLOAD_DIR` | `./data/uploads` | корень медиа (`avatars/`, `voice/`) |
| `PUBLIC_BASE_URL` | `http://localhost:8080` | префикс абсолютных URL аватаров/войсов |
| `MAX_AVATAR_BYTES` | `5242880` | лимит аватара (5 МиБ) |
| `MAX_UPLOAD_BYTES` | `10485760` | лимит multipart (войс ≤10 МиБ) |
| `AUTH_RATE_LIMIT` / `AVAILABILITY_RATE_LIMIT` | `30` / `120` | per-IP per-minute |
| `CORS_ORIGINS` | `*` | разрешённые origin |
| `AUTO_MIGRATE` | `true` | миграции при старте |
| `ALLOW_DELETE_OTHERS_MESSAGES` | `true` | удаление чужих сообщений для всех; видно в `/client-config` |
| `ADMIN_SECRET` | пусто | пусто = админ-API выключен |
| `MESSAGE_KEY` | пусто | 64-hex / Base64-32B; пусто → ключ из `JWT_SECRET` |
| `GITHUB_CLIENT_ID` / `GITHUB_CLIENT_SECRET` | пусто | оба нужны для GitHub-входа |
| `GITHUB_CALLBACK_URL` | `http://localhost:8080/api/v1/auth/github/callback` | должен совпадать с OAuth App |
| `GITHUB_SCOPE` | `read:user` | |
| `TURN_URLS` | список `turn:…` через запятую | TURN для `/rtc/config`; в оригинале зашиты LAN/Radmin-адреса — **в форке замени на свои** |
| `TURN_USERNAME` / `TURN_CREDENTIAL` | `goping` / `gopingturn` | креды coturn |

Клиентская переменная: `GOPING_API_URL` — базовый URL сервера для WPF-клиента
(default `http://localhost:8080`).

### Чеклист форка

1. Свои `JWT_SECRET`, `ADMIN_SECRET`, **`MESSAGE_KEY`** (сразу, отдельный).
2. Свой `PUBLIC_BASE_URL` (IP/домен, видимый друзьям) — иначе ссылки на
   аватары/войсы будут битыми у других машин.
3. Свой coturn + `TURN_URLS`/`TURN_USERNAME`/`TURN_CREDENTIAL`, если звонки
   не только в одной локалке.
4. GitHub OAuth App со своим `GITHUB_CALLBACK_URL` (если нужен вход через GitHub).
5. В клиенте: имя/брендинг, репозиторий обновлений (`UpdateService.DefaultGitHubRepo`
   захардкожен, плюс `make-release.ps1`), `GOPING_API_URL` по умолчанию. Встроенная
   консоль (`AppLog`, `HostControlService`) умеет запускать сервер, coturn в WSL и
   Radmin VPN прямо из приложения.

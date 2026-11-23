# GoPing API

Contract for GoPing clients and forks. Covers REST, WebSocket, encryption and server configuration.

- Base URL: `http://<host>:8080` (configurable).
- IDs are UUID strings. Timestamps use ISO-8601, for example `2026-05-23T12:00:00Z`.
- Authenticate with `Authorization: Bearer <accessToken>` on `/api/v1/**`, except the public paths listed below.
- Errors use one envelope:

```json
{ "error": { "code": "VALIDATION_ERROR", "message": "...", "details": { "text": "..." }, "requestId": "..." } }
```

Error codes include `UNAUTHORIZED`, `INVALID_SERVER_TOKEN`, `INVALID_REFRESH_TOKEN`, `NOT_FOUND`, `INVALID_ID`, `VALIDATION_ERROR`, `USERNAME_TAKEN` (409), `FORBIDDEN`, `DELETE_OTHERS_DISABLED` (403), `INVALID_PIN_SCOPE`, `MESSAGE_DAMAGED`, `AVATAR_TOO_LARGE` and `VOICE_TOO_LARGE` (413), `UNSUPPORTED_MEDIA_TYPE` (415), `INVALID_VOICE`, `RATE_LIMITED` (429 with `Retry-After`), `ADMIN_DISABLED` (503), `ADMIN_UNAUTHORIZED`, `GITHUB_NOT_CONFIGURED` (503), `GITHUB_AUTH_FAILED` (401), `GITHUB_STATE_EXPIRED` (410), `GITHUB_ALREADY_LINKED` (409) and `DB_UNAVAILABLE` (503). Every response has `X-Request-ID`.

## 1. Authentication

There are no passwords or registration. Use a server token or GitHub OAuth.

### POST `/api/v1/auth/token` (public)

```json
{ "token": "GOPING-XXXXXXXXXXXX" }
```

The admin issues tokens (section 7). The first login activates the token and creates user `ping_xxxxxxxx` with nickname `Ping XXXX`. Later logins with the same token open the same account.

### POST `/api/v1/auth/refresh` (public)

```json
{ "refreshToken": "..." }
```

Each refresh returns a new token pair and revokes the old refresh token. Reusing a revoked refresh token revokes all sessions of that user.

### POST `/api/v1/auth/logout` (public)

```json
{ "refreshToken": "..." }
```

Returns `204` and revokes the session.

### AuthResponse

```json
{
  "user": { "id": "...", "username": "...", "nickname": "...", "bio": "...",
            "avatarUrl": "...", "createdAt": "...", "updatedAt": "...",
            "online": true, "lastSeenAt": "..." },
  "tokenType": "Bearer",
  "accessToken": "<JWT>",
  "refreshToken": "<opaque>",
  "expiresIn": 900,
  "expiresAt": "2026-05-23T12:15:00Z"
}
```

Access tokens are HS256 JWTs signed with `JWT_SECRET`. Claims include `sub`, `token_use: "access"`, `iss`, `iat`, `nbf`, `exp` and `jti`. Default lifetime is 15 minutes (`ACCESS_TOKEN_TTL`). The same token is used for WebSocket. Refresh tokens are 32 random bytes in Base64URL. Only their SHA-256 hash is stored. Default lifetime is 30 days (`REFRESH_TOKEN_TTL`).

### GitHub OAuth

The client never sees the OAuth code.

| Method | Path | Description |
|---|---|---|
| GET | `/api/v1/auth/github/start` | Returns `authorizeUrl`, `state`, `expiresAt`. State lives 10 minutes. Returns 503 `GITHUB_NOT_CONFIGURED` when OAuth is not set up. |
| GET | `/api/v1/auth/github/callback?code&state` | Browser redirect target. The server exchanges the code, loads the profile and returns an HTML page. No tokens are returned here. |
| GET | `/api/v1/auth/github/poll?state=...` | Client polling. Returns `{"status":"pending","auth":null}` or `{"status":"ready","auth":AuthResponse}`. State is consumed once. 410 means expired, 401 means failed. |

First login creates a user from the GitHub login and profile. Accounts match on numeric `github_id`. Nickname and avatar can be changed later in the client.

Rate limits are per IP in a 60 second window. Auth and admin paths allow 30 requests per minute (`AUTH_RATE_LIMIT`). Username checks allow 120 (`AVAILABILITY_RATE_LIMIT`). Exceeding a limit returns 429.

## 2. Users

UserDto fields: `id`, `username`, `nickname`, `bio`, `avatarUrl`, `createdAt`, `updatedAt`, `online`, `lastSeenAt`.

| Method | Path | Body or parameters | Response |
|---|---|---|---|
| GET | `/api/v1/users/me` | none | `{"user": UserDto}` |
| PATCH | `/api/v1/users/me` | `{"nickname"?, "username"?, "bio"?}` | `{"user": UserDto}` |
| GET | `/api/v1/users/search?q=...` | username substring, case insensitive | `{"users": [UserDto]}`, up to 20, without self |
| GET | `/api/v1/users/username-available?username=...` | public | `{"username", "available", "reason": "invalid" \| "reserved" \| "taken" \| null}` |
| POST | `/api/v1/users/me/avatar` | multipart field `avatar` | `{"user": UserDto}` |
| DELETE | `/api/v1/users/me/avatar` | none | `{"user": UserDto}` |
| GET | `/api/v1/users/{id}` | UUID | `{"user": UserDto}` |
| GET | `/api/v1/users/username/{username}` | username | `{"user": UserDto}` |

Nickname is at most 50 characters and not unique. Username matches `^[a-z][a-z0-9_]{2,31}$`, is unique and has reserved names such as `admin`, `root` and `goping`. Bio is at most 300 characters. Avatars are JPEG, PNG or GIF, up to 4096 by 4096 and 5 MiB. There is no friend list, direct chats are created from a username.

## 3. Chats and messages

ChatDto fields: `id`, `kind` (`direct` or `favorites`), `title`, `username`, `avatarUrl`, `createdAt`, `peerUserId`, `lastMessageText`, `lastMessageAt`, `peerOnline`, `peerLastSeenAt`, `peerTyping`.

MessageDto fields: `id`, `chatId`, `sender` with `id`, `username`, `nickname`, `avatarUrl`, `text`, `algorithm`, `kind` (`text`, `voice` or `call`), `createdAt`, `replyTo` or null, `status` (`sent` or `read`), `pinScope` (`me`, `chat` or null), `editedAt`, `forwardedFromSenderNickname`, `forwardedFromText`.

The `status` field is relative to the viewer. `read` appears on your own messages after the peer reads them. `nextCursor` is currently always null.

| Method | Path | Body or parameters | Response |
|---|---|---|---|
| GET | `/api/v1/chats?limit=30` | limit 1 to 100 | `{"items": [ChatDto], "nextCursor": null}` |
| POST | `/api/v1/chats` | `{"username": "peer"}` | 201 ChatDto, idempotent, sends `chat.updated` |
| POST | `/api/v1/chats/{chatId}/typing` | none | 204, TTL 5 seconds, WS `typing` |
| POST | `/api/v1/chats/{chatId}/read` | none | 204, WS `messages.read` |
| GET | `/api/v1/chats/{chatId}/messages?limit=50` | limit 1 to 100 | `{"items": [MessageDto], ...}`, newest window |
| GET | `/api/v1/chats/{chatId}/pins` | none | `{"items": [MessageDto], ...}`, all visible pins |
| POST | `/api/v1/chats/{chatId}/messages` | `{"text", "replyToId"?}` | 201 MessageDto |
| POST | `/api/v1/chats/{chatId}/messages/voice` | multipart, section 5 | 201 MessageDto |
| PATCH | `/api/v1/chats/{chatId}/messages/{messageId}` | `{"text"}` | 200 MessageDto, own text messages only, sets `editedAt` |
| POST | `/api/v1/chats/{chatId}/messages/forward` | `{"fromChatId", "messageId"}` | 201 MessageDto in the target chat |
| DELETE | `/api/v1/chats/{chatId}/messages/{messageId}` | `{"forEveryone": bool}`, default false | 204 |
| POST | `/api/v1/chats/{chatId}/messages/{messageId}/pin` | `{"scope": "me" \| "chat"}` | 204 |
| DELETE | `/api/v1/chats/{chatId}/messages/{messageId}/pin` | `{"scope": "me" \| "chat"}` | 204 |

Text length is 1 to 4000 code points. Replies must be in the same chat. `GET /chats` also sends a presence heartbeat and creates a `Favorites` self chat for notes and forwarding to yourself. With `forEveryone=true`, delete removes the message for everyone. Deleting other people's messages requires `ALLOW_DELETE_OTHERS_MESSAGES=true`, otherwise the server returns 403 `DELETE_OTHERS_DISABLED`. With `false`, the message is hidden only for you. Forwarding decrypts the original and encrypts it again for the target chat. Damaged messages return 400 `MESSAGE_DAMAGED`. Forwarded messages include `forwardedFromSenderNickname` and `forwardedFromText` with up to 300 characters. Pin scope `me` is visible only to you, `chat` to everyone in the chat. Calls are not a separate REST resource. The server writes `kind:"call"` messages with `text` set to `{"status":"ended"|"missed"|"declined"|"cancelled","durationSec":N}` and delivers them as normal `message.created` events.

## 4. Presence, config and RTC

| Method | Path | Response |
|---|---|---|
| POST | `/api/v1/presence/heartbeat` | 204. Online means an active WebSocket or a heartbeat newer than 25 seconds. |
| GET | `/api/v1/client-config` | `{"allowDeleteOthersMessages": bool}` |
| GET | `/api/v1/rtc/config` | `{"iceServers": [...]}`. The TURN entry appears only when `TURN_URLS` is set. |
| GET | `/healthz` | Public, no prefix. `{"status":"ok"}` |
| GET | `/readyz` | Public. `{"status":"ready"}` or 503 `DB_UNAVAILABLE` |

Public media paths are served from `UPLOAD_DIR` with `Cache-Control: public, max-age=86400`:

- `/media/avatars/<uuid>.<jpg|png|gif>`
- `/media/voice/<uuid>.<ext>`

`avatarUrl` in responses is absolute, using `PUBLIC_BASE_URL`. GitHub users may have a GitHub avatar URL.

## 5. Voice messages

POST `/api/v1/chats/{chatId}/messages/voice` uses `multipart/form-data` with these fields:

- `audio`: the file
- `durationSec`: optional integer from 0 to 3600
- `replyToId`: optional UUID

The file must be non-empty and at most 10 MiB. Allowed formats are webm, ogg, mp3, wav, m4a and mp4. mp4 and m4a are stored as `.m4a`. Other formats return 415. Files are saved to `UPLOAD_DIR/voice/<uuid>.<ext>`. The stored message has `kind:"voice"` and encrypted `text` equal to `{"path":"/media/voice/<uuid>.<ext>","durationSec":N}`. API and WebSocket responses replace `path` with an absolute URL built from `PUBLIC_BASE_URL`.

## 6. WebSocket `/api/v1/ws`

Connect to `ws://host:8080/api/v1/ws?token=<accessToken>`. The JWT is checked during the handshake, there is no login frame. On connect the server sends a presence heartbeat, broadcasts `presence` to peers and delivers pending call invites. When the last socket closes, the user goes offline and `presence` is broadcast. Multiple sessions per user are supported.

Client to server frames:

| `type` | Fields | Effect |
|---|---|---|
| `ping` | none | Returns `pong` |
| `typing` | `chatId` | Broadcasts `typing` |
| `read` | `chatId` | Marks as read, broadcasts `messages.read` |
| `call.invite` | `chatId`, `callId` (UUID from client) | Creates the call in memory, sends `call.invite` to the peer |
| `call.accept` | `callId` | Peer receives `call.accepted` |
| `call.reject` | `callId` | Peer receives `call.rejected`, chat gets declined |
| `call.end` | `callId`, `reason?` default `hangup` | Peer receives `call.ended`, chat gets ended or missed |
| `call.signal` | `callId`, `kind` (`offer`, `answer`, `ice`), `data` | Relayed to the peer as is |
| `call.rejoin` | `callId` | Peer receives `call.peer-rejoined` so ICE restart can run |

Unknown types are ignored.

Server to client frames:

| `type` | Fields | Recipients |
|---|---|---|
| `pong` | none | Requester |
| `message.created` | `chatId`, `message` | Chat members, plus `chat.updated` |
| `message.edited` | `chatId`, `message` | Chat members, plus `chat.updated` |
| `message.deleted` | `chatId`, `messageId`, `forEveryone` | Whole chat if forEveryone, otherwise only the deleter |
| `message.pinned` | `chatId`, `messageId`, `scope`, `actorId`, `pinned` | Actor only for scope me, otherwise the chat |
| `chat.updated` | `chat` | Each chat member, also on chat creation |
| `typing` | `chatId`, `userId` | Chat members except the sender |
| `messages.read` | `chatId`, `readerId`, `readAt`, `upToMessageId?` | Chat members |
| `presence` | `userId`, `online`, `lastSeenAt?` | All peers of the user |
| `call.invite` | `callId`, `chatId`, `fromUserId`, `toUserId` | Callee, redelivered on reconnect for calls up to 10 minutes old |
| `call.accepted`, `call.rejected` | `callId`, `chatId`, `fromUserId`, `toUserId` | Peer |
| `call.ended` | `callId`, `chatId?`, `fromUserId?`, `toUserId?`, `reason` | Peer, or both at the 10 minute TTL |
| `call.signal` | `callId`, `chatId`, `fromUserId`, `toUserId`, `kind`, `data` | Peer |
| `call.peer-rejoined` | `callId`, `chatId`, `fromUserId`, `toUserId` | Peer |

Call state lives in memory only and expires after 10 minutes. A server restart sends `call.ended` with reason `server-restart`. Media goes peer to peer through WebRTC and is never seen by the server.

Call flow example:

1. A sends `{"type":"call.invite","chatId":"...","callId":"<uuid>"}`.
2. B receives `call.invite` and sends `call.accept`.
3. A receives `call.accepted`, creates an RTCPeerConnection and sends a `call.signal` offer.
4. B answers with kind `answer`, both sides exchange `ice` signals.
5. Either side sends `call.end` with reason `hangup`.

## 7. Admin tokens

Send header `X-Admin-Secret` with the value of `ADMIN_SECRET`. This is not a Bearer token. An empty `ADMIN_SECRET` returns 503 `ADMIN_DISABLED`.

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/api/v1/admin/tokens` | `{"label"?}` up to 80 characters | 201 `{"token": AdminTokenDto, "value": "GOPING-..."}`, raw value shown once |
| GET | `/api/v1/admin/tokens` | none | `{"items": [AdminTokenDto]}` |
| DELETE | `/api/v1/admin/tokens/{id}` | none | 204, revokes the token |

AdminTokenDto fields: `id`, `prefix` (`GOPING-` plus the first 4 characters), `label`, `userId` (null before activation), `createdAt`, `activatedAt`, `lastUsedAt`, `revokedAt`.

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = "<your ADMIN_SECRET>" } `
  -ContentType "application/json" -Body '{"label":"friend"}'
```

## 8. Message encryption

This is encryption at rest, not end to end. The client sends plaintext over TLS, the server encrypts before writing to SQLite and decrypts when serving chat members.

- Algorithm: GOST R 34.12-2015 Kuznyechik in CTR mode with GOST R 34.13-2015, plus encrypt then MAC with HMAC-SHA256 over nonce and ciphertext. Each message uses a random nonce.
- Database stores Base64 `ciphertext`, `nonce`, `mac` and the string `GOST34.12-2015/KUZNYECHIK/CTR`, which is also exposed as `MessageDto.algorithm`. Voice and call payloads are encrypted JSON strings in the same way.
- Keys are derived as SHA-256(label, 0x00, material) with labels `goping/message-encryption/v1` and `goping/message-authentication/v1`. Material is `MESSAGE_KEY` as 64 hex characters or Base64 of 32 bytes. If it is empty, the bytes of `JWT_SECRET` are used. Changing `JWT_SECRET` without a separate `MESSAGE_KEY` makes old messages unreadable.
- A MAC mismatch replaces the text with a damaged message marker. Forwarding it returns 400 `MESSAGE_DAMAGED`.

## 9. Server configuration

Environment variables can also be set in a `.env` file in the working directory.

| Variable | Default | Purpose |
|---|---|---|
| `HTTP_PORT` | `8080` | HTTP port |
| `DATABASE_PATH` | `./data/goping.db` | SQLite file, migrations V1 to V8 |
| `JWT_SECRET` | none | Required, at least 32 characters |
| `JWT_ISSUER` | `goping` | JWT issuer |
| `ACCESS_TOKEN_TTL` | `PT15M` | Access token lifetime |
| `REFRESH_TOKEN_TTL` | `P30D` | Must be longer than access lifetime |
| `UPLOAD_DIR` | `./data/uploads` | Root for `avatars/` and `voice/` |
| `PUBLIC_BASE_URL` | `http://localhost:8080` | Prefix for absolute media URLs |
| `MAX_AVATAR_BYTES` | `5242880` | Avatar limit, 5 MiB |
| `MAX_UPLOAD_BYTES` | `10485760` | Multipart limit, voice up to 10 MiB |
| `AUTH_RATE_LIMIT` | `30` | Auth requests per minute per IP |
| `AVAILABILITY_RATE_LIMIT` | `120` | Username checks per minute per IP |
| `CORS_ORIGINS` | `*` | Allowed origins |
| `AUTO_MIGRATE` | `true` | Apply migrations on startup |
| `ALLOW_DELETE_OTHERS_MESSAGES` | `true` | Allow deleting other people's messages, exposed in `/client-config` |
| `ADMIN_SECRET` | empty | Empty disables the admin API |
| `MESSAGE_KEY` | empty | 64 hex characters or Base64 of 32 bytes, derived from `JWT_SECRET` when empty |
| `GITHUB_CLIENT_ID` | empty | GitHub OAuth client ID |
| `GITHUB_CLIENT_SECRET` | empty | GitHub OAuth client secret |
| `GITHUB_CALLBACK_URL` | `http://localhost:8080/api/v1/auth/github/callback` | Must match the OAuth App |
| `GITHUB_SCOPE` | `read:user` | Requested scope |
| `TURN_URLS` | comma separated `turn:...` | TURN servers for `/rtc/config` |
| `TURN_USERNAME` | `goping` | coturn username |
| `TURN_CREDENTIAL` | `gopingturn` | coturn credential |

The WPF client uses `GOPING_API_URL` to select the server, default `http://localhost:8080`.

### Fork checklist

1. Set your own `JWT_SECRET`, `ADMIN_SECRET` and a separate `MESSAGE_KEY`.
2. Set `PUBLIC_BASE_URL` to an address your friends can reach, or avatar and voice links will break.
3. Set up coturn and `TURN_URLS`, `TURN_USERNAME`, `TURN_CREDENTIAL` if calls leave the local network.
4. Create a GitHub OAuth App and set `GITHUB_CALLBACK_URL` if you want GitHub login.
5. In the client, change the branding, the update repository in `UpdateService.DefaultGitHubRepo` and in `make-release.ps1`, and the default `GOPING_API_URL`. The in-app console uses `AppLog` and `HostControlService` to start the server, coturn in WSL and Radmin VPN.

# GoPing server

REST and WebSocket server for GoPing. Java 21, Spring Boot 3.3, SQLite, JWT. Messages are stored encrypted with GOST R 34.12-2015 (Kuznyechik) in CTR mode and HMAC-SHA256. The full API contract is in [docs/API.md](../../docs/API.md).

## Quick start

```powershell
cd server
copy .env.example .env
.\mvnw.cmd spring-boot:run
```

Set `JWT_SECRET` (at least 32 characters) and `ADMIN_SECRET` in `.env`. The server listens on `http://localhost:8080` and creates its database in `./data/`.

Health checks:

```powershell
curl http://localhost:8080/healthz
curl http://localhost:8080/readyz
```

Build a jar:

```powershell
.\mvnw.cmd -B -DskipTests package
java -jar target\goping-server-1.0.2.jar
```

Run tests with `.\mvnw.cmd test`.

## Configuration

Environment variables are read from `.env` automatically.

| Variable | Default | Purpose |
|---|---|---|
| `HTTP_PORT` | `8080` | HTTP port |
| `DATABASE_PATH` | `./data/goping.db` | SQLite file |
| `JWT_SECRET` | none | Required, at least 32 characters |
| `JWT_ISSUER` | `goping` | Token issuer |
| `ACCESS_TOKEN_TTL` | `PT15M` | Access token lifetime |
| `REFRESH_TOKEN_TTL` | `P30D` | Refresh token lifetime |
| `ADMIN_SECRET` | none | Admin API secret. Empty disables token issuing |
| `MESSAGE_KEY` | none | Message encryption key. Derived from `JWT_SECRET` when empty |
| `GITHUB_CLIENT_ID` | none | GitHub OAuth client ID |
| `GITHUB_CLIENT_SECRET` | none | GitHub OAuth client secret |
| `GITHUB_CALLBACK_URL` | `http://localhost:8080/api/v1/auth/github/callback` | OAuth callback |
| `UPLOAD_DIR` | `./data/uploads` | Uploaded avatars, voice and media files |
| `PUBLIC_BASE_URL` | `http://localhost:8080` | Base for absolute media URLs |
| `MAX_FILE_BYTES` | `52428800` | Multipart limit and max size for voice and media uploads |
| `MAX_TEXT_LENGTH` | `4000` | Max message length in code points |
| `MAX_VOICE_SECONDS` | `300` | Max voice message duration |
| `MAX_CALL_MINUTES` | `0` | Max call length, 0 means unlimited |
| `LOG_LINES` | `400` | In-memory log lines kept for the admin logs API |
| `CORS_ORIGINS` | `*` | Allowed origins |
| `AUTH_RATE_LIMIT` | `30` | Auth requests per minute per IP |
| `AVAILABILITY_RATE_LIMIT` | `120` | Username checks per minute per IP |

The four limit values can be changed at runtime with `POST /api/v1/admin/limits` without a restart. Clients receive them in a `limits` WebSocket frame on connect and after each change.

If GitHub credentials are missing, GitHub endpoints return `503 GITHUB_NOT_CONFIGURED`. Token login still works.

## API additions in 1.0.2

| Method | Path | Header or body | Response |
|---|---|---|---|
| POST | `/api/v1/chats/{chatId}/messages/media` | multipart `file`, `kind` (`photo`, `video` or `file`) | 201 MessageDto |
| POST | `/api/v1/chats/{chatId}/polls` | `{"question", "options": [string]}` | 201 PollDto |
| GET | `/api/v1/chats/{chatId}/polls/{pollId}` | none | 200 PollDto |
| POST | `/api/v1/chats/{chatId}/polls/{pollId}/vote` | `{"optionId"}` | 200 PollDto |
| GET | `/api/v1/admin/logs?lines=200` | `X-Admin-Secret` | `{"lines": [string]}`, capped at 1000 lines |
| POST | `/api/v1/admin/limits` | `X-Admin-Secret`, optional `{"maxTextLength"?, "maxVoiceSeconds"?, "maxCallMinutes"?, "maxFileBytes"?}` | limits DTO |

PollDto fields are `id`, `question`, `options` (`id`, `text`, `votes`), `totalVotes`, `closed`, `votedOptionIdSet` and `myVoteOptionId`. Poll create and vote also send a `poll.updated` frame to chat members. `GET /api/v1/chats` items include `unreadCount` from the stored read timestamp, and `POST /api/v1/chats/{chatId}/read` clears it.

```json
{ "maxTextLength": 4000, "maxVoiceSeconds": 300, "maxCallMinutes": 0, "maxFileBytes": 52428800 }
```

## Server tokens

A token looks like `GOPING-Ab3_x-9Qw2Z0`. The server stores only SHA-256 of it. The raw value is shown once at creation. The first login with a token creates a user, later logins open the same account. Revoked tokens stop working immediately.

Issue a token:

```powershell
$body = @{ label = "for Alice" } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET } `
  -ContentType "application/json" -Body $body
```

List and revoke:

```powershell
Invoke-RestMethod http://localhost:8080/api/v1/admin/tokens `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET }

Invoke-RestMethod -Method Delete `
  -Uri http://localhost:8080/api/v1/admin/tokens/<id> `
  -Headers @{ "X-Admin-Secret" = $env:ADMIN_SECRET }
```

`X-Admin-Secret` is a separate secret. It is never sent to clients.

## GitHub OAuth

1. Create an OAuth App in GitHub developer settings.
2. Set the callback URL to `http://localhost:8080/api/v1/auth/github/callback`.
3. Set `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` in `.env`.
4. The client calls `/auth/github/start`, opens the returned URL, then polls `/auth/github/poll` until the login is ready.

Nickname and avatar are taken from GitHub on first login and can be changed later in the client.

## Encryption

Messages are encrypted before they are written to SQLite. Each message uses a random 8-byte nonce. Integrity is checked with HMAC-SHA256 over nonce and ciphertext. Encryption keys are derived from `MESSAGE_KEY` or from `JWT_SECRET`. Set `MESSAGE_KEY` to keep old messages readable if `JWT_SECRET` changes.

Encryption is at rest. Use HTTPS through a reverse proxy if the server is reachable outside localhost.

## Notes

- SQLite runs in WAL mode with foreign keys enabled.
- Migrations live in `src/main/resources/db/migration` and are applied in name order.
- Do not commit `.env`, `data/` or session files.

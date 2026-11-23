# GoPing

**GoPing - a local messenger for you and your friends, with login via GitHub or a server token!**

> GoPing — мой личный клиент для меня и моих друзей. Хочешь такой же для своей
> компании? Форкай и переделывай под себя: полный контракт сервера —
> [docs/API.md](../docs/API.md), гайд для форков — в [корневом README](../README.md#форки-сделай-свой-мессенджер).

GoPing is a small local messenger: a desktop client on Windows (WPF) and a REST
API server (Spring Boot + SQLite). You and your friends run it on your own
machine(s); you talk to each other over the local network. Messages are stored
encrypted with the Russian standard **GOST R 34.12-2015 "Kuznyechik"** in CTR
mode (GOST R 34.13-2015) plus HMAC-SHA256 integrity protection.

## Login

- **GitHub** — one click. The browser flow (`/auth/github/start`,
  `/auth/github/callback`, `/auth/github/poll`) fills the avatar and nickname
  automatically on first login. Both stay editable afterwards.
- **Server token** — a one-time `GOPING-XXXXXXXXXXXX` token issued by the
  server admin (`/api/v1/admin/tokens`, protected by `X-Admin-Secret`).
  The token is hashed on the server (SHA-256), shown once, and activated once:
  the first `/api/v1/auth/token` call creates the user
  (`ping_xxxxxxxx` / `Ping XXXX`); all later calls with the same token log into
  that same user.

## Layout

```
GoPing/
├── server/          Java 21 + Spring Boot API (SQLite, JWT, GitHub OAuth, server tokens)
└── GoPing.Messenger/  WPF desktop client (minimalist flat UI, GitHub button + token field)
```

- `server/README.md` — API, env vars, admin token flow, security
- `GoPing.Messenger/README.md` — client build, login, session storage

## Quick start

Server:

```powershell
cd server
copy .env.example .env    # set JWT_SECRET and ADMIN_SECRET
.\mvnw.cmd spring-boot:run
```

Client:

```powershell
cd ..
dotnet run --project GoPing.Messenger
```

Then: issue a token from the server admin API (or use GitHub when
`GITHUB_CLIENT_ID` / `GITHUB_CLIENT_SECRET` are configured) and sign in.

## Build & test

- Server: `cd server && .\mvnw.cmd test`
- Client: `dotnet build GoPing.Messenger/GoPing.Messenger.csproj`

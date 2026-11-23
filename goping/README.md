# GoPing

GoPing is a local messenger with a Windows WPF client and a Spring Boot REST server. You run the server yourself and connect over LAN or VPN. Messages are stored encrypted with GOST R 34.12-2015 (Kuznyechik) in CTR mode plus HMAC-SHA256.

## 1.0.2

Adds polls, media uploads, live limits, server-side unread counts, an admin panel and a revised updater. The REST and WebSocket contract, including the new endpoints, `limits` frames and `unreadCount` field, is in [docs/API.md](../docs/API.md).

## Login

- GitHub: the browser flow fills the avatar and nickname on first login. You can edit both later.
- Server token: a one-time `GOPING-...` value issued by the admin. The server stores only its SHA-256 hash. The first login creates a user, later logins use the same account.

## Layout

```
server/            Java 21, Spring Boot, SQLite, REST and WebSocket
GoPing.Messenger/  Windows WPF client
installer/         Inno Setup installer and release scripts
```

## Quick start

```powershell
cd server
copy .env.example .env
.\mvnw.cmd spring-boot:run
```

Then, from the `goping` folder:

```powershell
dotnet run --project GoPing.Messenger
```

Set `GOPING_API_URL` to point the client at another server.

## Docs

- [server/README.md](server/README.md)
- [GoPing.Messenger/README.md](GoPing.Messenger/README.md)
- [API reference](../docs/API.md)

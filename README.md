# GoPing

GoPing is a small messenger for you and your friends. The Windows client talks to a server you run yourself. Messages are stored encrypted on that server. Sign in with GitHub or with a token issued by the server admin.

## Features

- 1:1 audio and video calls over P2P WebRTC. The server only relays signaling.
- Screen sharing with a custom source picker, Windows Graphics Capture (WGC) and a GDI fallback.
- Voice messages with a live recording waveform and playback progress.
- Chat tools: edit, forward, reply, pins, delete for self or everyone, search.
- Messages encrypted at rest with GOST R 34.12-2015 (Kuznyechik) in CTR mode plus HMAC-SHA256.
- Updates from GitHub Releases, with an optional beta channel.

### 1.0.2

- Reliable in-app updater with zip and installer support, sha256 checks and release notes after update.
- Rounded design and native Windows notifications.
- Voice playback speed up to x4 and a listened marker.
- Polls in chats.
- Media uploads with compression and a Save as popup.
- Chat list with unread counts and date separators.
- Server list for switching between saved servers.
- Admin panel for tokens, live limits and recent server logs.
- Cache clearing from settings.

## Repository layout

```
goping/
  server/              Java 21, Spring Boot, SQLite (REST and WebSocket)
  GoPing.Messenger/    Windows WPF client (.NET 10, WebView2)
  installer/           Inno Setup installer and release scripts
docs/API.md            API reference
LICENSE                MIT license
```

## Quick start

### Server

```powershell
cd goping/server
copy .env.example .env
.\mvnw.cmd spring-boot:run
```

Set `JWT_SECRET` (at least 32 characters) and `ADMIN_SECRET` in `.env` first. The server listens on `http://localhost:8080` and creates its database in `./data/goping.db`. Check it with `curl http://localhost:8080/healthz`.

### Client

```powershell
dotnet run --project goping/GoPing.Messenger
```

Set `GOPING_API_URL` to use a server, for example a Radmin VPN address. Without it the client falls back to `http://26.68.224.240:8080`.

### Sign in

1. Ask the admin for a `GOPING-...` token, or configure GitHub OAuth with `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET`.
2. Paste the token into the client or use the GitHub button. The first token login creates the user, later logins open the same account.

Friends connect over LAN or VPN, each with their own token.

## Installer

```powershell
powershell -ExecutionPolicy Bypass -File goping\installer\build-installer.ps1
```

This produces `goping/installer/output/GoPing-Setup-v1.0.2-win-x64.exe` (about 54 MB). It is a native Inno Setup installer with custom wizard graphics. It installs per user to `%LOCALAPPDATA%\Programs\GoPing` without admin rights, creates Start menu and optional desktop shortcuts, and adds an uninstall entry. Chats and settings in `%LOCALAPPDATA%\GoPing` are kept on uninstall.

Silent runs:

```powershell
GoPing-Setup-v1.0.2-win-x64.exe /VERYSILENT /DIR="C:\Path"
"%LOCALAPPDATA%\Programs\GoPing\unins000.exe" /VERYSILENT /NORESTART
```

## Updates

The update source is hardcoded to `https://github.com/RealBibus/GoPing`.

1. Increase `<Version>` in `goping/GoPing.Messenger/GoPing.Messenger.csproj`.
2. Run `powershell -ExecutionPolicy Bypass -File goping\installer\make-release.ps1`. It creates `GoPing-win-x64.zip` and `GoPing-win-x64.zip.sha256`.
3. Create a GitHub release tagged `v<version>` and attach both files. Also attach `GoPing-Setup-<version>-win-x64.exe` for manual installs, for example `GoPing-Setup-v1.0.2-win-x64.exe`. The updater uses the zip first and falls back to the installer exe.

In the client, open Settings, then Updates. New versions download in the background. The card shows release notes and an Install button. The beta checkbox includes pre-releases. After an update the success notice and release notes are shown again.

## Forking

1. Fork the repository.
2. Set your own secrets in `.env`: `JWT_SECRET`, `ADMIN_SECRET`, `MESSAGE_KEY`. If `MESSAGE_KEY` is empty, it is derived from `JWT_SECRET`, and changing `JWT_SECRET` makes old messages unreadable. For calls over the internet, configure coturn with `TURN_URLS`, `TURN_USERNAME` and `TURN_CREDENTIAL`. See [docs/API.md](docs/API.md).
3. Change the client name, icon, colors and text as needed. REST, WebSocket and call logic are separated in `Services/ApiClient.cs`, `Services/RealtimeClient.cs` and `Services/CallService.cs`.
4. To write a different client, use [docs/API.md](docs/API.md) as the server contract.
5. For your own update feed, change `UpdateService.DefaultGitHubRepo` and the repo name in `make-release.ps1`.

Keep a credit to the original project. See [LICENSE](LICENSE).

## Documentation

- [docs/API.md](docs/API.md) (REST, WebSocket, encryption, server config)
- [goping/server/README.md](goping/server/README.md)
- [goping/GoPing.Messenger/README.md](goping/GoPing.Messenger/README.md)

## Build and test

```powershell
cd goping/server
.\mvnw.cmd test
cd ..
dotnet build goping/GoPing.Messenger/GoPing.Messenger.csproj
powershell -ExecutionPolicy Bypass -File goping\installer\build-installer.ps1
```

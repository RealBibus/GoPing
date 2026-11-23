# GoPing client

Windows desktop client for GoPing. Built with WPF on .NET 10 and WebView2.

## Requirements

- Windows 10 or 11, x64
- .NET 10 SDK for building
- WebView2 Runtime (preinstalled on most Windows 10 and 11 systems)

## Build and run

From the `goping` folder:

```powershell
dotnet build GoPing.Messenger/GoPing.Messenger.csproj
dotnet run --project GoPing.Messenger
```

The client uses `http://localhost:8080` by default. To use another server:

```powershell
$env:GOPING_API_URL = "http://192.168.1.10:8080"
dotnet run --project GoPing.Messenger
```

## Sign in

Use a `GOPING-...` server token or the GitHub button. GitHub login requires `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` on the server. The first token login creates the user, later logins open the same account.

## Features

- 1:1 audio and video calls over WebRTC, with fullscreen overlay and speaking indicator
- Screen sharing with WGC or GDI capture and a quality picker
- Voice messages with hold-to-record and waveform playback
- Chats with edit, forward, reply, pins and search
- In-app console for server, TURN and Radmin VPN controls
- Updates from GitHub Releases

## Data locations

- Session: `%LOCALAPPDATA%\GoPing\session.dat`, protected with DPAPI
- Logs: `%LOCALAPPDATA%\GoPing\` (`app-console.log`, `call.log`, `voice-debug.log`, `pin-debug.log`)
- Installed app: `%LOCALAPPDATA%\Programs\GoPing`

## Layout

```
App.xaml, App.xaml.cs        application styles and startup
LoginWindow.xaml(.cs)        GitHub and token login
MainWindow.xaml(.cs)         chats, calls, settings and console
Assets/call/                 WebView2 call page
Assets/voice/                WebView2 voice recorder
Services/ApiClient.cs        REST calls
Services/RealtimeClient.cs   WebSocket events
Services/CallService.cs      WebRTC call bridge
Services/UpdateService.cs    GitHub Releases updater
```

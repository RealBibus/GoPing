# GoPing Changelog

All notable changes to this project are documented in this file.

## [1.0.0] - 2026-05-23 (Unreleased worktree snapshot)

This release focuses on stabilizing 1:1 P2P calls, improving screen sharing, polishing call UX, and preparing the messenger for distribution.

### Highlights

- **WebView2-based call engine** for 1:1 audio/video/screen WebRTC calls, with WPF UI controls and bridge signaling.
- **Reliable screen sharing** with a custom WPF source picker, WGC capture, GDI fallback, configurable capture mode, and configurable FPS.
- **Call UX improvements**: full-screen call overlay, speaking ring, call stats, peer media-state indicators, reconnecting status, and chat call-log cards.
- **Settings redesign**: cleaner card-based settings for notifications, screen sharing, and experimental options.
- **Voice messages implemented end-to-end**, temporarily disabled in the composer UI pending release validation.
- **Installer packaging**: publish script and Inno Setup installer script for Windows x64 distribution.
- **In-app auto-update from GitHub Releases**: check, download, verify, install and restart without leaving the messenger.

### Added

#### Calls & Media
- Migrated call media flow to a WebView2 call engine (`Assets/call/call.html`, `Assets/call/call.js`) bridged from WPF `CallService`.
- Added in-call stats bridge/UI and peer media-state signaling/UI.
- Added screen sharing through the custom WPF picker with native C# capture frames sent to WebView2 (`screenFrame` / `startScreenShareExternal` / `pushScreenFrame`).
- Added WGC screen capture (`WgcScreenCapturer`) with detailed HRESULT diagnostics.
- Added GDI capture fallback for systems where WGC is unavailable or fails.
- Added capture mode setting: **Auto / WGC / GDI** (`AppSettings.ScreenCaptureMode`, Settings UI, `CallService`).
- Added screen share FPS setting: **30–60 FPS** (default 30, step 5) in settings and capture pipeline.
- Added cursor drawing to GDI screen capture.
- Added active-peer-connection check before starting screen share (`hasActivePc` / JS result bridge) to avoid "share failed: no active call".
- Added in-call remote volume popup (styled like a message popup) with a **0–400%** gain slider via WebAudio GainNode.
- Added in-call screen share settings popup (quality preset + FPS) styled to match the app; default share quality is HD.

#### Chat & UX
- Added message editing: own text messages can be edited from the message actions menu; server re-seals the ciphertext and marks `edited_at`, realtime `message.edited` updates both clients, edited label shown in the time footer.
- Added message forwarding with a chat picker: server decrypts the original and re-encrypts it fresh into the target chat (no ciphertext copy), forwarded messages show a “Forwarded from …” header.
- Added dedicated pinned-messages endpoint `GET /api/v1/chats/{chatId}/pins` so the pinned bar shows all pins, even messages older than the loaded history window.
- **Re-enabled voice messages with a hold-to-record UX**: hold the mic button to record, release to auto-send. Added a live voice-frequency bar (28 bands from a WebAudio `AnalyserNode` streamed from `recorder.js` to WPF), a recording timer with pulsing dot, cancel (✕) and stop-and-send (✓) buttons, mouse capture so releasing outside the button still sends, a minimum-duration guard, and a 5-minute auto-stop/auto-send cap.
- Voice bubbles now render a deterministic waveform (seeded by message id) with a playback-progress highlight driven by `MediaPlayer.Position`, plus duration label.
- Added call-log/missed-call chat cards aligned as normal message bubbles (left/right by sender) instead of centered status cards.
- Redesigned Settings window into card-based sections: Notifications, Screen sharing, Experimental.
- Added voice message support across server and client:
  - Server: `POST /api/v1/chats/{chatId}/messages/voice` multipart upload (`audio`, `durationSec`, `replyToId`), stored under `UPLOAD_DIR/voice/...`.
  - Message kind: `voice`, encrypted JSON payload with `path` and `durationSec`.
  - Client: hidden WebView2 MediaRecorder (`Assets/voice/recorder.*`), upload via `ApiClient.SendVoiceAsync`, voice bubble playback UI.
  - Chat preview displays “Voice message”.

#### Distribution
- Added Inno Setup installer script: `goping/installer/GoPing.iss`.
- Added publish/installer build script: `goping/installer/build-installer.ps1`.
- Added assembly/version metadata in `GoPing.Messenger.csproj`.
- Added **in-app auto-updater via GitHub Releases** (`Services/UpdateService.cs`): checks the latest release tag against the app version, downloads the `GoPing-win-x64.zip` asset with progress, verifies the optional `.sha256` checksum, extracts to staging, then applies files and restarts GoPing automatically via a detached updater script.
- Added **Updates** settings card: current version, Check for updates, Download & install with progress bar. GitHub repo is hardcoded to `https://github.com/RealBibus/GoPing` in `UpdateService.DefaultGitHubRepo` (no input field; old empty settings values self-heal).
- Added silent update check on startup and an accent "update available" button in the bottom quick-actions bar that opens Settings.
- Added a **beta updates** checkbox (`UseBetaUpdates`): beta channel reads all recent GitHub releases including pre-releases, stable channel uses `/releases/latest`.
- Updates now **download automatically** in the background; the Settings card shows the **release notes/changelog** from the GitHub release body and an **Install update** button that applies the download and restarts the app.
- Added an **in-app Console** (bottom-left quick actions): live messenger actions/errors, Copy/Clear/Close, Log file at `%LOCALAPPDATA%\GoPing\app-console.log`, and one-click **Start server + TURN**, **Start TURN** (coturn in WSL), **Start Radmin VPN**. Radmin VPN is auto-started at launch when installed, and the console shows a hint on how to join the same Radmin network.
- Added release packaging script `goping/installer/make-release.ps1`: publish → `output/GoPing-win-x64.zip` + `.sha256`, ready to attach to a GitHub release tag `v{version}`.
- Added repository `README.md` (features, quick start, release flow, fork guide) and full `docs/API.md` for forks: REST, WebSocket events, auth/JWT/refresh rotation, GitHub OAuth poll flow, voice upload, GOST at-rest encryption, admin tokens, server env config and fork checklist.

### Changed

- Call signaling remains on WebSocket `/api/v1/ws`; media remains P2P WebRTC. TURN config is obtained from `GET /api/v1/rtc/config` with STUN fallback.
- Call WebView2 bridge is initialized through `CallService.InitializeAsync(CallWebView)`; CallId comparison is normalized (case-insensitive, strips `-`).
- Screen share capture loop now accounts for capture/encode elapsed time for more stable FPS.
- JS screen capture stream now uses the configured target FPS instead of a hardcoded 10 FPS.
- Call/connection failures avoid Windows MessageBox-style interruptions in call flow; in-app status/UI is used.

### Fixed

- Fixed voice recording failures: the recorder WebView2 now lives in an always-active layer of the window (was inside the collapsed call overlay), assets are served from a secure `https://goping.local` virtual host instead of `file://` so `getUserMedia` is allowed, the recorder is warmed up at startup so the first press records immediately, and JS errors are safe strings instead of `[object Object]`. Full diagnostics go to `%LOCALAPPDATA%\GoPing\voice-debug.log`; UI error text is shortened to 180 chars.
- Fixed the "object" error that killed both recording and the live waveform: `recorder.js` posted a JSON **string**, which WebView2 wrapped in quotes so C# could not read `type`/`bands` (every message threw `InvalidOperationException: ... element has type 'String'`). Messages are now posted as objects, and the C# bridge also accepts the old string form for compatibility.
- Removed Russian text from user-facing UI; all UI strings are now English (source comments may remain Russian).
- Fixed pinned messages not displaying: the pinned bar now loads pins from the dedicated `/pins` endpoint instead of only scanning the last 100 loaded messages, refreshes after pin/unpin and realtime pin events, and stays in sync when a pinned message is edited. Pin failures now show an in-app status and are logged to `%LOCALAPPDATA%\GoPing\pin-debug.log`.
- Fixed screen share stop/start breaking the next share: `sender.replaceTrack(null/track)` is used instead of `removeTrack` (no renegotiation breakage), and remote video is cleared on track `onmute` as well as `onended` so viewers no longer get stuck on “Reconnecting…” after a share stops.
- Fixed caller getting stuck on `Calling` by normalizing CallId handling.
- Fixed WGC initialization failure on systems where `D3D11CreateDevice` with `BGRA_SUPPORT` returns `0x887A002D` by falling back to `flags=0` and using safe D3D11 device/context marshaling.
- Fixed GDI capture issues including `CopyPixelOperation` enum validation and cross-thread WebView2 posting.
- Fixed screen share starting before an active WebRTC peer connection was ready.
- Fixed GitHub login post-login crash caused by `ScreenShareFpsSlider_ValueChanged` firing before call service initialization (null guard).
- Improved GitHub login and call/screen-share diagnostic logging (`%LOCALAPPDATA%\GoPing\call.log`).

### Temporarily Disabled

- (none) — voice message composer UI has been re-enabled (see Added → Chat & UX).

### Known Issues / Validation Notes

- **Self-audio on same-PC two-client test**: when calling between two clients on the same PC, users may expect to hear themselves as a WebView2/WebRTC health signal. This behavior needs explicit retest/confirmation in the current WebView2 call engine.
- **WebView2 Runtime** is required on target machines. Most Windows 10/11 systems have it; installer users may need to install Evergreen WebView2 Runtime if missing.
- **Inno Setup 6** is required to compile `GoPing.iss` into a `.exe` installer. The provided build script publishes the app even if ISCC is not installed.

### Build / Packaging

Client debug build:

```powershell
dotnet build goping/GoPing.Messenger/GoPing.Messenger.csproj -c Debug
```

Server compile:

```powershell
cd goping/server
.\mvnw.cmd -q -DskipTests compile
```

Publish + installer (Windows x64):

```powershell
powershell -ExecutionPolicy Bypass -File goping/installer/build-installer.ps1
```

If Inno Setup 6 is not installed, the script leaves a publish folder at `goping/installer/publish/win-x64` and prints the ISCC command to compile the installer later.

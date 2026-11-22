@echo off
setlocal
REM Start coturn in WSL for GoPing WebRTC calls
wsl -d Ubuntu-24.04 -u root -- bash -c "pgrep -x turnserver >/dev/null || turnserver -c /etc/goping/turnserver.conf -o"
if errorlevel 1 (
  echo [WARN] Failed to start coturn in WSL. Calls may fail without TURN.
) else (
  echo [OK] coturn started/ready.
)
cd /d "%~dp0server"
call mvnw.cmd spring-boot:run

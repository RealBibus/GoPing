using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace GoPing.Services;

/// <summary>
/// Управляет локальным сервером GoPing, coturn (WSL) и Radmin VPN — прямо из мессенджера.
/// </summary>
public static class HostControlService
{
    private static Process? _serverProcess;
    private static readonly HttpClient Health = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static string GopingDir => FindGopingDir() ?? AppContext.BaseDirectory;
    public static string ServerDir => Path.Combine(GopingDir, "server");
    public static string ApiUrl => Environment.GetEnvironmentVariable("GOPING_API_URL")?.TrimEnd('/') ?? "http://localhost:8080";

    public const string RadminHint =
        "To call over the internet, install Radmin VPN and join the same virtual network " +
        "as your friends (ask the network owner for its name and password). " +
        "Radmin VPN is started automatically here when it is installed.";

    private static string? FindGopingDir()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "server", "mvnw.cmd")) &&
                File.Exists(Path.Combine(dir, "start-server.cmd")))
                return dir;
        }
        string? overrideDir = Environment.GetEnvironmentVariable("GOPING_SERVER_DIR");
        return string.IsNullOrWhiteSpace(overrideDir) ? null : overrideDir;
    }

    public static async Task<bool> IsServerRunningAsync()
    {
        try
        {
            using var response = await Health.GetAsync(ApiUrl + "/healthz");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Запускает coturn в WSL с конфигом проекта. Возвращает true при успехе.</summary>
    public static async Task<bool> StartTurnAsync()
    {
        try
        {
            AppLog.Info("Starting coturn (TURN) in WSL…");
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-d Ubuntu-24.04 -u root -- bash -c " +
                            "\"pgrep -x turnserver >/dev/null || turnserver -c /etc/goping/turnserver.conf -o\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) { AppLog.Error("Could not start WSL."); return false; }
            string output = await process.StandardOutput.ReadToEndAsync();
            string errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                AppLog.Error("coturn failed: " + Clean(errors.Length > 0 ? errors : output));
                return false;
            }
            AppLog.Info("coturn (TURN) is running on port 3478.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("coturn start failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Запускает Spring Boot сервер, если он ещё не поднят, и ждёт /healthz.</summary>
    public static async Task StartServerAsync()
    {
        if (await IsServerRunningAsync())
        {
            AppLog.Info("Server is already running (" + ApiUrl + ").");
            return;
        }
        string serverDir = ServerDir;
        string mvnw = Path.Combine(serverDir, "mvnw.cmd");
        if (!File.Exists(mvnw))
        {
            AppLog.Error("mvnw.cmd not found at " + serverDir);
            return;
        }

        await StartTurnAsync();
        AppLog.Info("Starting GoPing server from " + serverDir + "…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{mvnw}\" spring-boot:run\"",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _serverProcess = new Process { StartInfo = psi };
            _serverProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Info("[server] " + e.Data); };
            _serverProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Warn("[server] " + e.Data); };
            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1500);
                if (await IsServerRunningAsync())
                {
                    AppLog.Info("Server is up: " + ApiUrl);
                    return;
                }
            }
            AppLog.Warn("Server did not answer /healthz in 90s — see [server] lines above.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Server start failed: " + ex.Message);
        }
    }

    public static bool IsRadminRunning()
        => Process.GetProcessesByName("RvRvpnGui").Length > 0 ||
           Process.GetProcessesByName("RadminVPN").Length > 0;

    public static string? FindRadmin()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Radmin VPN\RvRvpnGui.exe",
            @"C:\Program Files\Radmin VPN\RvRvpnGui.exe",
            @"C:\Program Files (x86)\Radmin VPN\RadminVPN.exe",
            @"C:\Program Files\Radmin VPN\RadminVPN.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void StartRadminIfNeeded(bool logIfMissing = false)
    {
        if (IsRadminRunning()) return;
        string? path = FindRadmin();
        if (path is null)
        {
            if (logIfMissing) AppLog.Warn("Radmin VPN is not installed. " + RadminHint);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            AppLog.Info("Radmin VPN started automatically.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start Radmin VPN: " + ex.Message);
        }
    }

    private static string Clean(string text)
    {
        // убираем UTF-16-шум из вывода wsl.exe
        return new string(text.Where(c => c == '\r' || c == '\n' || c >= ' ').ToArray()).Trim();
    }
}

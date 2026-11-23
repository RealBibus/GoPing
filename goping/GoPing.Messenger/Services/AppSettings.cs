using System.IO;
using System.Text.Json;
using GoPing.Models;

namespace GoPing.Services;

public sealed class AppSettings
{
    public bool RaiseWindowOnIncomingCall { get; set; } = true;
    public bool ShowMessageNotifications { get; set; } = true;
    public bool MicrophonePermissionGranted { get; set; }
    public int ScreenShareFps { get; set; } = 30;
    public string ScreenCaptureMode { get; set; } = "Auto";
    public string ScreenShareQuality { get; set; } = "HD";
    public bool UseBetaUpdates { get; set; }

    private string _updateGitHubRepo = UpdateService.DefaultGitHubRepo;
    /// <summary>Репозиторий обновлений захардкожен в UpdateService; старые пустые значения чинятся сами.</summary>
    public string UpdateGitHubRepo
    {
        get => _updateGitHubRepo;
        set => _updateGitHubRepo = string.IsNullOrWhiteSpace(value)
            ? UpdateService.DefaultGitHubRepo
            : value.Trim();
    }

    public List<ServerEntry> Servers { get; set; } = new();
    public string ActiveServerId { get; set; } = string.Empty;
    public bool ExperimentalTyping { get; set; }
    public bool NativeNotifications { get; set; } = true;
    public int MaxTextLength { get; set; } = 4000;
    public int MaxVoiceSeconds { get; set; } = 300;
    public int MaxCallMinutes { get; set; }
    public long MaxFileBytes { get; set; } = 52428800;

    /// <summary>Returns the selected server, or null when no server is selected.</summary>
    public ServerEntry? ActiveServer
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ActiveServerId)) return null;
            return Servers?.FirstOrDefault(s =>
                string.Equals(s.Id, ActiveServerId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds or replaces a server and persists the change immediately.</summary>
    public void AddOrUpdateServer(ServerEntry server)
    {
        if (server is null) return;
        Servers ??= new List<ServerEntry>();
        int index = Servers.FindIndex(s =>
            string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Servers[index] = server;
        else Servers.Add(server);
        if (string.IsNullOrWhiteSpace(ActiveServerId)) ActiveServerId = server.Id;
        AppSettingsStore.Save(this);
    }

    /// <summary>Removes a server and clears the active selection when needed.</summary>
    public bool RemoveServer(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || Servers is null) return false;
        int index = Servers.FindIndex(s =>
            string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        Servers.RemoveAt(index);
        if (string.Equals(ActiveServerId, serverId, StringComparison.OrdinalIgnoreCase))
            ActiveServerId = string.Empty;
        AppSettingsStore.Save(this);
        return true;
    }

    /// <summary>Selects the active server and persists the change immediately.</summary>
    public void SetActiveServer(string serverId)
    {
        ActiveServerId = serverId?.Trim() ?? string.Empty;
        AppSettingsStore.Save(this);
    }

    /// <summary>Applies server-provided limits while keeping safe values.</summary>
    public void ApplyLimits(LimitsConfig? limits)
    {
        if (limits is null) return;
        if (limits.MaxTextLength > 0) MaxTextLength = limits.MaxTextLength;
        if (limits.MaxVoiceSeconds > 0) MaxVoiceSeconds = limits.MaxVoiceSeconds;
        if (limits.MaxCallMinutes >= 0) MaxCallMinutes = limits.MaxCallMinutes;
        if (limits.MaxFileBytes > 0) MaxFileBytes = limits.MaxFileBytes;
        AppSettingsStore.Save(this);
    }
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GoPing");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Json);
            AppSettings settings = loaded ?? new AppSettings();
            settings.Servers ??= new List<ServerEntry>();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            settings.Servers ??= new List<ServerEntry>();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));
        }
        catch
        {
            // ignore - settings are best-effort
        }
    }
}

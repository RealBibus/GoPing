using System.IO;
using System.Text.Json;

namespace GoPing.Services;

public sealed class AppSettings
{
    public bool RaiseWindowOnIncomingCall { get; set; } = true;
    public bool ShowMessageNotifications { get; set; } = true;
    public bool MicrophonePermissionGranted { get; set; }
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
            return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));
        }
        catch
        {
            // ignore - settings are best-effort
        }
    }
}

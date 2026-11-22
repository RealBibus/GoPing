using System.IO;
using System.Text;

namespace GoPing.Services;

public static class CallLogger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoPing", "call.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            System.Diagnostics.Debug.WriteLine("[Call] " + line.TrimEnd());
        }
        catch { }
    }

    public static string GetLogPath() => LogPath;
}

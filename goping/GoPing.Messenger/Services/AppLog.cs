using System.IO;
using System.Text;

namespace GoPing.Services;

/// <summary>Внутренняя консоль мессенджера: всё, что делает приложение, и все ошибки.</summary>
public static class AppLog
{
    public sealed record Entry(string Time, string Level, string Message)
    {
        public string Line => $"{Time} [{Level}] {Message}";
    }

    private const int MaxEntries = 600;
    private static readonly object Gate = new();
    private static readonly Queue<Entry> Entries = new();
    private static string? _filePath;

    public static event Action<Entry>? Added;

    public static void Info(string message) => Add("INFO", message);
    public static void Warn(string message) => Add("WARN", message);
    public static void Error(string message) => Add("ERROR", message);
    public static void Debug(string message) => Add("DEBUG", message);

    public static IReadOnlyList<Entry> Snapshot()
    {
        lock (Gate) return Entries.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
        Added?.Invoke(new Entry(DateTime.Now.ToString("HH:mm:ss"), "INFO", "Console cleared."));
    }

    private static void Add(string level, string message)
    {
        string text = message.Replace('\r', ' ').Replace('\n', ' ');
        var entry = new Entry(DateTime.Now.ToString("HH:mm:ss"), level, text);
        lock (Gate)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > MaxEntries) Entries.Dequeue();
        }
        try
        {
            _filePath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GoPing", "app-console.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath, entry.Line + System.Environment.NewLine, Encoding.UTF8);
        }
        catch { }
        Added?.Invoke(entry);
    }
}

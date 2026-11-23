using System.IO;

namespace GoPing.Services;

/// <summary>
/// Clears local WebView2 caches and application memory caches. Browser cache
/// folders are removed on the next launch because they are locked while running.
/// </summary>
public static class CacheService
{
    private const string PendingFileName = "clear-cache.pending";

    private static readonly string[] CacheFolderNames =
    {
        "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache",
        "CacheStorage", "Service Worker"
    };

    public static Action? InMemoryCleanup { get; set; }

    private static string PendingPath => Path.Combine(UpdateService.UpdateDir, PendingFileName);

    /// <summary>Marks all local caches for cleanup and clears in-memory caches now.</summary>
    public static long Clear()
    {
        InMemoryCleanup?.Invoke();
        try { File.WriteAllText(PendingPath, DateTimeOffset.UtcNow.ToString("O")); }
        catch { /* best effort */ }
        return EstimateSize();
    }

    /// <summary>Called early at startup, before any WebView2 instance is created.</summary>
    public static int ApplyPendingClear()
    {
        try
        {
            if (!File.Exists(PendingPath)) return 0;
            int removed = 0;
            string baseDir = AppContext.BaseDirectory;
            foreach (string webviewRoot in Directory.EnumerateDirectories(baseDir, "*WebView2*", SearchOption.TopDirectoryOnly))
            {
                foreach (string folder in Directory.EnumerateDirectories(webviewRoot, "*", SearchOption.AllDirectories))
                {
                    if (!CacheFolderNames.Any(n => string.Equals(Path.GetFileName(folder), n, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    try { Directory.Delete(folder, recursive: true); removed++; }
                    catch { /* locked or already gone */ }
                }
            }
            File.Delete(PendingPath);
            return removed;
        }
        catch
        {
            return 0;
        }
    }

    public static long EstimateSize()
    {
        long total = 0;
        try
        {
            string baseDir = AppContext.BaseDirectory;
            foreach (string webviewRoot in Directory.EnumerateDirectories(baseDir, "*WebView2*", SearchOption.TopDirectoryOnly))
            {
                foreach (string folder in Directory.EnumerateDirectories(webviewRoot, "*", SearchOption.AllDirectories))
                {
                    if (!CacheFolderNames.Any(n => string.Equals(Path.GetFileName(folder), n, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        catch { /* ignore */ }
        return total;
    }
}

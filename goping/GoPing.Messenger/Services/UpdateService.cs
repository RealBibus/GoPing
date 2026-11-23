using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace GoPing.Services;

/// <summary>Информация о доступном обновлении из GitHub Releases.</summary>
public sealed record UpdateInfo(
    string Version,
    string TagName,
    string ReleaseName,
    string? Notes,
    string AssetName,
    string DownloadUrl,
    long AssetSize,
    string? Sha256Url,
    bool IsPrerelease);

/// <summary>
/// Встроенный автоапдейтер: проверяет последний релиз GitHub,
/// скачивает zip, проверяет sha256 (если есть), распаковывает
/// и заменяет файлы приложения через внешний cmd-скрипт с перезапуском.
/// </summary>
public static class UpdateService
{
    /// <summary>Репозиторий обновлений GoPing (зашит в клиент, настройка не требуется).</summary>
    public const string DefaultGitHubRepo = "https://github.com/RealBibus/GoPing";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Путь к уже скачанному ассету при автоскачке обновления.</summary>
    public static string? DownloadedPath { get; private set; }

    public static void ClearDownloaded() => DownloadedPath = null;

    public static void RememberDownload(string path) => DownloadedPath = path;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string UpdateDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GoPing", "update");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GoPing-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Принимает "owner/repo" или полный URL репозитория/релиза.</summary>
    public static string NormalizeRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return string.Empty;
        string value = repo.Trim();
        value = value.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("http://github.com/", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("github.com/", "", StringComparison.OrdinalIgnoreCase);
        int cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) value = value[..cut];
        value = value.Trim('/');
        // отбрасываем хвосты вида /releases/latest, /tree/main и т.п.
        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) value = $"{parts[0]}/{parts[1]}";
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    /// <summary>
    /// Возвращает UpdateInfo, если свежий релиз новее текущей версии, иначе null.
    /// beta=true — учитываются pre-release (бета) сборки.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string repo, bool beta = false, CancellationToken ct = default)
    {
        string normalized = NormalizeRepo(repo);
        if (normalized.Length == 0 || !normalized.Contains('/'))
            throw new InvalidOperationException("Repository is missing.");

        string url = beta
            ? $"https://api.github.com/repos/{normalized}/releases?per_page=15"
            : $"https://api.github.com/repos/{normalized}/releases/latest";

        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No releases found for this repository.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // бета-канал: первый подходящий релиз, включая pre-release
            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                UpdateInfo? info = ParseRelease(release);
                if (info is not null) return info;
            }
            return null;
        }

        return ParseRelease(doc.RootElement);
    }

    private static UpdateInfo? ParseRelease(JsonElement root)
    {
        string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        string versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out Version? latest)) return null;
        if (latest <= CurrentVersion) return null;

        string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? tag : tag;
        string? notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
        bool prerelease = root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();

        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.GetArrayLength() == 0)
            throw new InvalidOperationException("Release has no attached files (attach GoPing-win-x64.zip).");

        JsonElement? chosen = null;
        JsonElement? anyZip = null;
        JsonElement? installer = null;
        string? sha256Url = null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                sha256Url = url;
                continue;
            }
            bool isZip = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (isZip && assetName.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                      && assetName.StartsWith("GoPing", StringComparison.OrdinalIgnoreCase))
                chosen = asset;
            else if (isZip && anyZip is null) anyZip = asset;
            else if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && installer is null)
                installer = asset;
        }

        JsonElement picked = chosen ?? anyZip ?? installer
            ?? throw new InvalidOperationException("Release has no zip/exe asset.");

        string pickedName = picked.GetProperty("name").GetString() ?? "update.zip";
        string pickedUrl = picked.GetProperty("browser_download_url").GetString() ?? "";
        long size = picked.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;

        string? matchedSha = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? "";
            if (string.Equals(assetName, pickedName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                matchedSha = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        return new UpdateInfo(
            latest.ToString(), tag, name, notes,
            pickedName, pickedUrl, size, matchedSha ?? sha256Url, prerelease);
    }

    /// <summary>Скачивает ассет в %LOCALAPPDATA%\GoPing\update, возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = Path.Combine(UpdateDir, info.AssetName);
        using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? info.AssetSize;

        await using var remote = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        byte[] buffer = new byte[1 << 16];
        long readTotal = 0;
        int read;
        while ((read = await remote.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0) progress?.Report(Math.Clamp(readTotal * 100.0 / total, 0, 100));
        }
        progress?.Report(100);
        return target;
    }

    /// <summary>Проверяет sha256, если к релизу приложен .sha256-файл. Без файла — true.</summary>
    public static async Task<bool> VerifyAsync(string filePath, UpdateInfo info, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.Sha256Url)) return true;
        string expected;
        try
        {
            string text = await Http.GetStringAsync(info.Sha256Url, ct);
            expected = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }
        catch
        {
            return true; // контрольная сумма недоступна — не блокируем обновление
        }

        await using var file = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(file, ct);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Распаковывает zip в staging и возвращает папку, где лежит GoPing.exe.</summary>
    public static string ExtractToStaging(string zipPath, UpdateInfo info)
    {
        string staging = Path.Combine(UpdateDir, $"staging-{info.Version}");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // zip может содержать вложенную папку publish — ищем GoPing.exe
        string? exe = Directory.EnumerateFiles(staging, "GoPing.exe", SearchOption.AllDirectories).FirstOrDefault();
        return exe is null ? staging : Path.GetDirectoryName(exe)!;
    }

    /// <summary>
    /// Запускает cmd-скрипт: ждёт завершения текущего процесса, копирует новые файлы
    /// поверх приложения, запускает GoPing.exe и удаляет сам себя.
    /// После вызова нужно завершить приложение (Application.Shutdown).
    /// </summary>
    public static void LaunchUpdaterAndRestart(string stagingDir)
    {
        string targetDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Path.Combine(targetDir, "GoPing.exe");
        int pid = Environment.ProcessId;
        string scriptPath = Path.Combine(UpdateDir, "apply-update.cmd");

        string script = $"""
            @echo off
            setlocal
            rem GoPing auto-update: wait for app exit, copy files, restart.
            :wait
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >NUL
                goto wait
            )
            robocopy "{stagingDir}" "{targetDir}" /E /IS /IT /NFL /NDL /NJH /NJS /NP >NUL
            start "" "{exePath}"
            endlocal
            del "%~f0"
            """;
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            WorkingDirectory = UpdateDir
        };
        Process.Start(psi);
    }
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace GoPing.Services;

public enum UpdateAssetKind
{
    Zip,
    Installer
}

/// <summary>Information about an available GitHub release.</summary>
public sealed record UpdateInfo(
    string Version,
    string TagName,
    string ReleaseName,
    string? Notes,
    string AssetName,
    string DownloadUrl,
    long AssetSize,
    string? Sha256Url,
    bool IsPrerelease,
    UpdateAssetKind AssetKind = UpdateAssetKind.Zip)
{
    public bool IsInstaller => AssetKind == UpdateAssetKind.Installer;
}

/// <summary>Notice written before applying an update and shown after restart.</summary>
public sealed record UpdateCompletionNotice(string Version, string? Notes);

/// <summary>
/// Built-in updater. Checks GitHub Releases, downloads a zip payload or a native
/// installer, verifies sha256 when present, applies it and restarts the client.
/// </summary>
public static class UpdateService
{
    /// <summary>Hardcoded update repository. No UI field is needed.</summary>
    public const string DefaultGitHubRepo = "https://github.com/RealBibus/GoPing";

    private const string MarkerFileName = "update-complete.json";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Path of the downloaded asset during automatic download.</summary>
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

    private static string MarkerPath => Path.Combine(UpdateDir, MarkerFileName);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GoPing-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Accepts "owner/repo" or a full repository/release URL.</summary>
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
        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) value = $"{parts[0]}/{parts[1]}";
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    /// <summary>Returns a newer release, or null when the current version is up to date.</summary>
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
            throw new InvalidOperationException("Release has no attached files. Attach GoPing-win-x64.zip or the setup exe.");

        JsonElement? preferredZip = null;
        JsonElement? anyZip = null;
        JsonElement? installer = null;
        string? genericSha = null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            bool isChecksum = assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                              || assetName.StartsWith("sha256", StringComparison.OrdinalIgnoreCase)
                              || assetName.StartsWith("checksum", StringComparison.OrdinalIgnoreCase);
            if (isChecksum)
            {
                genericSha = url;
                continue;
            }

            bool isZip = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            bool isExe = assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (isZip && assetName.StartsWith("GoPing", StringComparison.OrdinalIgnoreCase)
                      && assetName.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                preferredZip = asset;
            else if (isZip && anyZip is null)
                anyZip = asset;
            else if (isExe && installer is null)
                installer = asset;
        }

        JsonElement picked = preferredZip ?? anyZip ?? installer
            ?? throw new InvalidOperationException("Release has no zip or installer asset.");
        UpdateAssetKind kind = picked.ValueKind == JsonValueKind.Object &&
                               (picked.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? UpdateAssetKind.Installer
            : UpdateAssetKind.Zip;

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
            pickedName, pickedUrl, size, matchedSha ?? genericSha, prerelease, kind);
    }

    /// <summary>Downloads the selected asset into %LOCALAPPDATA%\GoPing\update.</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = Path.Combine(UpdateDir, Path.GetFileName(info.AssetName));
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
        RememberDownload(target);
        return target;
    }

    /// <summary>Verifies sha256 when the release includes a checksum file.</summary>
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
            return true;
        }

        await using var file = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(file, ct);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts a zip payload into staging and returns the folder with GoPing.exe.</summary>
    public static string ExtractToStaging(string zipPath, UpdateInfo info)
    {
        string staging = Path.Combine(UpdateDir, $"staging-{info.Version}");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        string? exe = Directory.EnumerateFiles(staging, "GoPing.exe", SearchOption.AllDirectories).FirstOrDefault();
        return exe is null ? staging : Path.GetDirectoryName(exe)!;
    }

    /// <summary>Applies a downloaded zip or installer and restarts the application.</summary>
    public static void ApplyAndRestart(UpdateInfo info, string downloadedPath)
    {
        WriteCompletionMarker(info);
        if (info.IsInstaller)
            LaunchInstallerAndRestart(downloadedPath);
        else
            LaunchUpdaterAndRestart(ExtractToStaging(downloadedPath, info));
    }

    private static void WriteCompletionMarker(UpdateInfo info)
    {
        var payload = new
        {
            version = info.Version,
            notes = info.Notes ?? string.Empty,
            appliedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Called on startup. Returns the notice only when the update was actually applied,
    /// so a failed install never claims success.
    /// </summary>
    public static UpdateCompletionNotice? ConsumeCompletionNotice()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(MarkerPath));
            string? versionText = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            string? notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() : null;
            if (Version.TryParse(versionText, out var target) && target <= CurrentVersion)
            {
                File.Delete(MarkerPath);
                return new UpdateCompletionNotice(target.ToString(), string.IsNullOrWhiteSpace(notes) ? null : notes);
            }
        }
        catch
        {
            // A damaged marker is ignored. Keeping it is harmless.
        }
        return null;
    }

    private static void LaunchUpdaterAndRestart(string stagingDir)
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
        StartHiddenScript(scriptPath);
    }

    private static void LaunchInstallerAndRestart(string installerPath)
    {
        string targetDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Path.Combine(targetDir, "GoPing.exe");
        int pid = Environment.ProcessId;
        string scriptPath = Path.Combine(UpdateDir, "apply-installer.cmd");

        string script = $"""
            @echo off
            setlocal
            rem GoPing auto-update: wait for app exit, run the native installer, restart.
            :wait
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >NUL
                goto wait
            )
            start /wait "" "{installerPath}" /VERYSILENT /NORESTART /NOCANCEL /CLOSEAPPLICATIONS
            start "" "{exePath}"
            endlocal
            del "%~f0"
            """;
        File.WriteAllText(scriptPath, script);
        StartHiddenScript(scriptPath);
    }

    private static void StartHiddenScript(string scriptPath)
    {
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

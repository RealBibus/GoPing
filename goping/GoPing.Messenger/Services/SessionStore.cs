using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoPing.Services;

public sealed record StoredSession(string? ServerToken, string? RefreshToken);

/// <summary>
/// Хранит серверный токен или refresh-токен в DPAPI-зашифрованном файле
/// текущего пользователя Windows.
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoPing");
    private static readonly string FilePath = Path.Combine(Folder, "session.dat");

    public static StoredSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            byte[] encrypted = File.ReadAllBytes(FilePath);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(plain, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(StoredSession session)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, encrypted);
        }
        catch
        {
            // Не роняем вход из-за ошибки записи на диск.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // Игнорируем.
        }
    }
}

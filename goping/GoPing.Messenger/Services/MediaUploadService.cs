using System.IO;

namespace GoPing.Services;

/// <summary>Compresses a local media file when possible and uploads it to the active chat.</summary>
public sealed class MediaUploadService
{
    private readonly ApiClient _api;
    private readonly MediaCompressionService _compression = new();

    public MediaUploadService(ApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Uploads a file, enforcing the configured byte limit before sending.</summary>
    public async Task UploadAsync(
        string chatId,
        string filePath,
        string kind,
        long maxFileBytes,
        IProgress<double>? progress)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file path is required.", nameof(filePath));

        progress?.Report(0);
        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        string mime = MimeForExtension(Path.GetExtension(filePath));
        string fileName = MediaCompressionService.SafeFileName(Path.GetFileName(filePath));

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mime, "image/gif", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var compressed = await _compression.CompressImageAsync(bytes);
                bytes = compressed.Bytes;
                mime = compressed.Mime;
                string renamed = Path.ChangeExtension(fileName, compressed.Extension);
                if (!string.IsNullOrWhiteSpace(renamed)) fileName = renamed;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Formats such as WebP and HEIC are not supported by System.Drawing on Windows.
                // Send the original file and let the server accept or reject it by extension.
                AppLog.Warn("Image compression skipped, uploading original: " + ex.Message);
            }
        }
        else if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            var compressed = await _compression.CompressVideoAsync(bytes, maxFileBytes, null);
            bytes = compressed.Bytes;
            mime = compressed.Mime;
            string renamed = Path.ChangeExtension(fileName, compressed.Extension);
            if (!string.IsNullOrWhiteSpace(renamed)) fileName = renamed;
        }

        if (maxFileBytes > 0 && bytes.LongLength > maxFileBytes)
            throw new ApiException("FILE_TOO_LARGE",
                $"The file is larger than the {maxFileBytes} byte server limit.");

        using var stream = new MemoryStream(bytes, writable: false);
        await _api.UploadMediaAsync(chatId, stream, fileName, mime, kind, progress);
    }

    private static string MimeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };
}

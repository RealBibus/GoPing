using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace GoPing.Services;

/// <summary>Prepares media files for upload without adding external dependencies.</summary>
public sealed class MediaCompressionService
{
    /// <summary>Downsamples and re-encodes images as JPEG, preserving GIF animations.</summary>
    public Task<(byte[] Bytes, string Mime, string Extension)> CompressImageAsync(
        byte[] input, int maxDimension = 1920, int quality = 82)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (input.Length == 0) throw new ArgumentException("The image is empty.", nameof(input));

        return Task.Run(() => CompressImage(input, Math.Max(1, maxDimension), Math.Clamp(quality, 1, 100)));
    }

    /// <summary>
    /// Media Foundation re-encoding is not used here because it would require COM
    /// interop setup outside this patch. The server-side maxFileBytes limit applies.
    /// </summary>
    public Task<(byte[] Bytes, string Mime, string Extension)> CompressVideoAsync(
        byte[] input, long maxBytes, IProgress<double>? progress = null)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        string extension = VideoExtension(input);
        return Task.FromResult((input, MimeForExtension(extension), extension));
    }

    /// <summary>Removes directories, control characters and illegal file name characters.</summary>
    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "upload.bin";
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if (char.IsControl(c) || c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0)
                continue;
            builder.Append(c);
        }
        string result = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "upload.bin" : result;
    }

    private static (byte[] Bytes, string Mime, string Extension) CompressImage(
        byte[] input, int maxDimension, int quality)
    {
        if (input.Length >= 3 && input[0] == (byte)'G' && input[1] == (byte)'I' && input[2] == (byte)'F')
            return (input, "image/gif", ".gif");

        bool looksLikeJpeg = input.Length >= 3 && input[0] == 0xFF && input[1] == 0xD8 && input[2] == 0xFF;
        using var source = new MemoryStream(input, writable: false);
        using Image image = Image.FromStream(source);

        int orientation = ReadOrientation(image);
        if (looksLikeJpeg && orientation <= 1
            && image.Width <= maxDimension && image.Height <= maxDimension
            && image.RawFormat.Guid == ImageFormat.Jpeg.Guid)
        {
            return (input, "image/jpeg", ".jpg");
        }

        if (orientation > 1) image.RotateFlip(RotateFlipFor(orientation));

        double scale = Math.Min(1.0, (double)maxDimension / Math.Max(image.Width, image.Height));
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        }

        using var output = new MemoryStream();
        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
        if (codec is null)
        {
            bitmap.Save(output, ImageFormat.Jpeg);
        }
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(output, codec, parameters);
        }
        return (output.ToArray(), "image/jpeg", ".jpg");
    }

    private static int ReadOrientation(Image image)
    {
        try
        {
            PropertyItem? item = image.GetPropertyItem(0x0112);
            if (item?.Value is { Length: > 0 } value) return value[0];
        }
        catch (ArgumentException)
        {
            // No EXIF orientation block on this image.
        }
        return 1;
    }

    private static RotateFlipType RotateFlipFor(int orientation) => orientation switch
    {
        2 => RotateFlipType.RotateNoneFlipX,
        3 => RotateFlipType.Rotate180FlipNone,
        4 => RotateFlipType.Rotate180FlipX,
        5 => RotateFlipType.Rotate90FlipX,
        6 => RotateFlipType.Rotate90FlipNone,
        7 => RotateFlipType.Rotate270FlipX,
        8 => RotateFlipType.Rotate270FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone
    };

    private static string VideoExtension(byte[] input)
    {
        if (input.Length >= 12 && input[4] == (byte)'f' && input[5] == (byte)'t'
            && input[6] == (byte)'y' && input[7] == (byte)'p') return ".mp4";
        if (input.Length >= 4 && input[0] == 0x1A && input[1] == 0x45
            && input[2] == 0xDF && input[3] == 0xA3) return ".webm";
        if (input.Length >= 12 && input[0] == (byte)'R' && input[1] == (byte)'I'
            && input[2] == (byte)'F' && input[3] == (byte)'F') return ".avi";
        return ".mp4";
    }

    private static string MimeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        _ => "video/mp4"
    };
}

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoPing.Services;

/// <summary>Одна картинка из локального набора эмодзи.</summary>
internal sealed record EmojiEntry(int Number, string Token, string FilePath);

/// <summary>
/// Локальный набор эмодзи из папки emojiP. Картинки лежат внутри SVG как base64 PNG,
/// поэтому достаём их напрямую — без внешних SVG-библиотек. В сообщениях эмодзи
/// передаются текстовыми токенами :emoji_N:, чтобы работало через обычный REST API.
/// </summary>
internal static class EmojiCatalog
{
    private const int MaxCachedImages = 256;

    private static readonly Regex FileNameRegex = new(@"^image\s+(\d+)\.svg$", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@":emoji_(\d+):", RegexOptions.Compiled);

    private static List<EmojiEntry>? _entries;
    private static readonly Dictionary<int, ImageSource?> ImageCache = new();
    private static Dictionary<int, string>? _unicodeMap;
    private static Dictionary<string, int>? _unicodeToNumber;
    private static string[]? _unicodePatterns;

    public static bool IsInstalled => DirectoryPath is not null;

    public static string? DirectoryPath { get; } = FindDirectory();

    public static IReadOnlyList<EmojiEntry> Entries => _entries ??= Load();

    private static List<EmojiEntry> Load()
    {
        var result = new List<EmojiEntry>();
        if (DirectoryPath is null) return result;

        try
        {
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "image *.svg"))
            {
                Match match = FileNameRegex.Match(Path.GetFileName(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
                {
                    result.Add(new EmojiEntry(number, $":emoji_{number}:", file));
                }
            }
        }
        catch
        {
            return result;
        }

        result.Sort((a, b) => a.Number.CompareTo(b.Number));
        return result;
    }

    private static string? FindDirectory()
    {
        string start = AppDomain.CurrentDomain.BaseDirectory;

        string nextToExe = Path.Combine(start, "emojiP");
        if (Directory.Exists(nextToExe)) return nextToExe;

        // При разработке папка лежит рядом с исходниками, а не в bin/.
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string projectFolder = Path.Combine(dir.FullName, "GoPing.Messenger", "emojiP");
            if (Directory.Exists(projectFolder)) return projectFolder;

            string plainFolder = Path.Combine(dir.FullName, "emojiP");
            if (Directory.Exists(plainFolder)) return plainFolder;
        }
        return null;
    }

    /// <summary>
    /// Необязательный файл emojiP/emoji-map.txt вида "42=😀". Если для картинки
    /// есть соответствие, в сообщение уходит Unicode-символ, который увидят все.
    /// локально же этот символ снова рисуется картинкой из пака.
    /// </summary>
    private static void EnsureUnicodeMap()
    {
        if (_unicodeMap is not null) return;

        _unicodeMap = new Dictionary<int, string>();
        _unicodeToNumber = new Dictionary<string, int>(StringComparer.Ordinal);

        if (DirectoryPath is not null)
        {
            string mapPath = Path.Combine(DirectoryPath, "emoji-map.txt");
            if (File.Exists(mapPath))
            {
                try
                {
                    foreach (string raw in File.ReadAllLines(mapPath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        if (int.TryParse(line[..eq].Trim(), out int key)
                            && line[(eq + 1)..].Trim() is { Length: > 0 } value)
                        {
                            _unicodeMap[key] = value;
                            _unicodeToNumber[value] = key;
                        }
                    }
                }
                catch
                {
                    _unicodeMap.Clear();
                    _unicodeToNumber.Clear();
                }
            }
        }

        // Сначала проверяем длинные последовательности (флаги, ZWJ-эмодзи),
        // чтобы 🇷🇺 не превратилось в одиночный 🇷.
        _unicodePatterns = _unicodeToNumber.Keys
            .OrderByDescending(static key => key.Length)
            .ToArray();
    }

    public static string? GetUnicode(int number)
    {
        EnsureUnicodeMap();
        return _unicodeMap!.TryGetValue(number, out string? unicode) ? unicode : null;
    }

    /// <summary>Номер картинки для Unicode-символа из файла соответствий.</summary>
    public static bool TryGetNumberForUnicode(string unicode, out int number)
    {
        EnsureUnicodeMap();
        return _unicodeToNumber!.TryGetValue(unicode, out number);
    }

    public static ImageSource? GetImage(int number)
    {
        if (ImageCache.TryGetValue(number, out ImageSource? cached)) return cached;

        ImageSource? image = null;
        EmojiEntry? entry = Entries.FirstOrDefault(e => e.Number == number);
        if (entry is not null)
        {
            try
            {
                string svg = File.ReadAllText(entry.FilePath);
                int start = svg.IndexOf("base64,", System.StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += "base64,".Length;
                    int end = svg.IndexOfAny(new[] { '"', '\'', '<', '\r', '\n' }, start);
                    if (end < 0) end = svg.Length;

                    byte[] bytes = System.Convert.FromBase64String(svg[start..end]);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
            catch
            {
                image = null;
            }
        }

        if (ImageCache.Count >= MaxCachedImages) ImageCache.Clear();
        ImageCache[number] = image;
        return image;
    }

    private readonly record struct EmojiMatch(int Index, int Length, int Number);

    /// <summary>
    /// Заменяет токены эмодзи и Unicode-символы из файла соответствий на картинки
    /// внутри сообщения. Отправка при этом идёт текстом, совместимым с любым клиентом.
    /// </summary>
    public static IEnumerable<Inline> BuildInlines(string text)
    {
        var matches = new List<EmojiMatch>();

        foreach (Match match in TokenRegex.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out int number))
            {
                matches.Add(new EmojiMatch(match.Index, match.Length, number));
            }
        }

        EnsureUnicodeMap();
        if (_unicodePatterns is { Length: > 0 })
        {
            foreach (string pattern in _unicodePatterns)
            {
                if (!_unicodeToNumber!.TryGetValue(pattern, out int number)) continue;

                int from = 0;
                while (true)
                {
                    int index = text.IndexOf(pattern, from, StringComparison.Ordinal);
                    if (index < 0) break;

                    matches.Add(new EmojiMatch(index, pattern.Length, number));
                    from = index + pattern.Length;
                }
            }
        }

        // Приоритет: более ранний и более длинный фрагмент.
        matches.Sort(static (a, b) =>
        {
            int byIndex = a.Index.CompareTo(b.Index);
            return byIndex != 0 ? byIndex : b.Length.CompareTo(a.Length);
        });

        int position = 0;
        foreach (EmojiMatch match in matches)
        {
            if (match.Index < position) continue;

            if (match.Index > position)
            {
                yield return new Run(text[position..match.Index]);
            }

            if (GetImage(match.Number) is { } image)
            {
                var picture = new Image
                {
                    Source = image,
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 2, -4)
                };
                yield return new InlineUIContainer(picture)
                {
                    BaselineAlignment = BaselineAlignment.Bottom
                };
            }
            else
            {
                yield return new Run(text.Substring(match.Index, match.Length));
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            yield return new Run(text[position..]);
        }
    }
}

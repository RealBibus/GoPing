using System.Globalization;
using System.Windows.Data;

namespace GoPing.Converters;

public sealed class InitialLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = (value as string)?.Trim() ?? string.Empty;
        if (text.Length == 0) return "?";
        return StringInfo.GetNextTextElement(text).ToUpperInvariant();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

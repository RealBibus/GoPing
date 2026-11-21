using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GoPing.Converters;

/// <summary>
/// Преобразует URL аватарки в BitmapImage для привязки к Image.Source.
/// </summary>
public sealed class AvatarImageSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null!;
        }

        try
        {
            var bitmap = new BitmapImage(new Uri(url));
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

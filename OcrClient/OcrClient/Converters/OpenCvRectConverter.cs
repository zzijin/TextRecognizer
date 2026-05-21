using System.Globalization;
using System.Windows.Data;

namespace OcrClient.UI.Converters;

public class OpenCvRectConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is OpenCvSharp.Rect cvRect)
            return new System.Windows.Rect(cvRect.X, cvRect.Y, cvRect.Width, cvRect.Height);
        return new System.Windows.Rect(0, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

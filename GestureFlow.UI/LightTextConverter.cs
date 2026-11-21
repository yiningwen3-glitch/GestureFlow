using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GestureFlow.UI;

public sealed class LightTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SolidColorBrush brush && brush.Color.G > 128;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

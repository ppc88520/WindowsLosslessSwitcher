using System.Globalization;
using System.Windows.Data;

namespace WindowsLosslessSwitcher.Converters;

/// <summary>Formats a day count as "1 day" / "7 days" for the cache-refresh chips.</summary>
public sealed class DaysLabelConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int days ? days == 1 ? Services.LocalizationService.Current["1 day"] : Services.LocalizationService.Current.Format("{0} days", days) : "";

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.FirstOrDefault(), targetType, parameter, culture);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

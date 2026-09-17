using System;
using Microsoft.UI.Xaml.Data;

namespace RuApp.Converters;

/// <summary>Binds an ISO timestamp to a compact relative label via RelativeTimeFormatter.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Services.RelativeTimeFormatter.Format(value as string ?? string.Empty, DateTimeOffset.UtcNow);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

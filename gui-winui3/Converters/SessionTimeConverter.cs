using System;
using Microsoft.UI.Xaml.Data;

namespace RuApp.Converters;

/// <summary>Binds an ISO timestamp to an absolute local time label via SessionTimeFormatter.</summary>
public sealed class SessionTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Services.SessionTimeFormatter.Format(value as string ?? string.Empty, DateTimeOffset.UtcNow);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

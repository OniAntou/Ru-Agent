using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.UI.Xaml.Markup;

namespace RuApp.Services;

public static class AppUnhandledExceptionPolicy
{
    public static bool Handle(Exception? exception, Action<Exception?> writeCrashEvent)
    {
        ArgumentNullException.ThrowIfNull(writeCrashEvent);

        if (IsRecoverable(exception))
        {
            return true;
        }

        writeCrashEvent(exception);
        return false;
    }

    private static bool IsRecoverable(Exception? exception)
    {
        return exception is IOException
            or WebSocketException
            or HttpRequestException
            or TaskCanceledException
            or ObjectDisposedException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException
            or XamlParseException;
    }
}

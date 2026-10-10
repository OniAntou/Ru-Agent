using System.Text;
using RuAgent.Protocol;

namespace RuAgent.Broker.Diagnostics;

/// <summary>
/// Bounded, redacted stderr diagnostics for tool-handler failures. Only the
/// request id and the exception type reach stderr: the reply contract and any
/// payload or message content stay untouched.
/// </summary>
internal static class BrokerDiagnosticLog
{
    public static void ToolHandlerFailed(AgentEnvelope request, Exception error)
    {
        try
        {
            Console.Error.WriteLine(
                $"ru.broker tool_handler_failed request_id={Bound(request.RequestId)} exception={error.GetType().Name}");
        }
        catch
        {
            // A diagnostic must never change the outcome of a tool request.
        }
    }

    private static string Bound(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var length = Math.Min(value.Length, 128);
        var builder = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            builder.Append(char.IsControl(character) ? '_' : character);
        }

        return builder.ToString();
    }
}

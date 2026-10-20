using System.Text.Json;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Optional header metadata must not wait for the normal HTTP timeout at turn completion.</summary>
internal static class TgSessionTitleReader
{
    internal static async Task<string?> ReadAsync(
        Func<CancellationToken, Task<JsonElement>> readSession,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            var doc = await readSession(deadline.Token).ConfigureAwait(false);
            if (doc.ValueKind == JsonValueKind.Object
                && doc.TryGetProperty("session", out var session)
                && session.ValueKind == JsonValueKind.Object
                && session.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(title.GetString()))
            {
                return title.GetString()!.Trim();
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Tui.TuiDebug.Log("header", "session title probe timed out");
            return null;
        }
    }
}

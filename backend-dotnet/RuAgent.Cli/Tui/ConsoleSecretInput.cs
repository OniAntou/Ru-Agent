using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>Minimal no-echo input for credentials outside the Terminal.Gui frame.</summary>
public static class ConsoleSecretInput
{
    private const int MaxSecretChars = 4096;
    public static async Task<string?> ReadAsync(
        TextWriter outWriter,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outWriter);
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !ReferenceEquals(outWriter, Console.Out))
        {
            return null;
        }

        await outWriter.WriteAsync(prompt).ConfigureAwait(false);
        await outWriter.FlushAsync().ConfigureAwait(false);

        var sb = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                await outWriter.WriteLineAsync().ConfigureAwait(false);
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            if ((key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                || key.KeyChar == '\x16')
            {
                var pasted = ChatCommandCatalog.SanitizePastedText(Images.CliClipboard.TryReadText(), MaxSecretChars - sb.Length);
                sb.Append(pasted);
                await outWriter.WriteAsync(pasted.Length > 0
                    ? $" (pasted {pasted.Length} chars)"
                    : " (clipboard empty — try right-click paste)").ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                continue;
            }

            if (char.IsControl(key.KeyChar) || sb.Length >= MaxSecretChars)
            {
                continue;
            }

            sb.Append(key.KeyChar);
        }

        await outWriter.WriteLineAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return sb.ToString();
    }

}

using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>Line-oriented fallback. It never owns the screen or reads console keys.</summary>
public sealed class PlainChatInput
{
    public async Task<string?> ReadLineWithContinuationAsync(
        TextReader input, TextWriter output, string prompt, string continuationPrompt,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await ReadSingleLineAsync(input, output, prompt, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (text.Length == 0) return null;
                break;
            }
            if (line.EndsWith('\\'))
            {
                text.AppendLine(line[..^1]);
                prompt = continuationPrompt;
                continue;
            }
            text.Append(line);
            break;
        }
        var result = text.ToString();
        return result;
    }

    public async Task<string?> ReadSingleLineAsync(
        TextReader input, TextWriter output, string prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteAsync(prompt).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }
}

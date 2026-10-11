using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>Line-oriented fallback. It never owns the screen or reads console keys.</summary>
public sealed class PlainChatInput
{
    private const int MaxHistory = 100;
    private readonly List<string> _history = [];
    public IReadOnlyList<string> History => _history;

    public void AddToHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || (_history.Count > 0 && _history[^1] == line)) return;
        _history.Add(line);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
    }

    public async Task<int> RunAsync(
        TextReader input, TextWriter output,
        Func<string, CancellationToken, Task<int?>> submit,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineWithContinuationAsync(input, output, "> ", "... ", cancellationToken)
                .ConfigureAwait(false);
            if (line is null) return 0;
            if (await submit(line, cancellationToken).ConfigureAwait(false) is { } exitCode)
                return exitCode;
        }
        return 0;
    }

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
        AddToHistory(result);
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

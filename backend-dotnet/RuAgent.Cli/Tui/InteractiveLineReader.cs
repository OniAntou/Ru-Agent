using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Cli.Tui;

public sealed class InteractiveLineReader
{
    private readonly List<string> _history = new();
    private static readonly string[] DefaultSlashCommands =
    [
        "/help",
        "/model",
        "/backend",
        "/fork",
        "/rewind",
        "/tree",
        "/stats",
        "/tokens",
        "/export",
        "/status",
        "/history",
        "/clear",
        "/exit",
        "/quit"
    ];

    public IReadOnlyList<string> History => _history;

    public void AddToHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
        {
            _history.Add(line);
        }
    }

    public static string? AutoComplete(string input, IEnumerable<string> candidates)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/'))
        {
            return null;
        }

        var matches = candidates
            .Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        return null;
    }

    public async Task<string?> ReadLineWithContinuationAsync(
        TextReader inReader,
        TextWriter outWriter,
        string primaryPrompt,
        string continuationPrompt,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        string prompt = primaryPrompt;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadSingleLineAsync(inReader, outWriter, prompt, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            // Check for line continuation with trailing backslash
            if (line.EndsWith('\\'))
            {
                sb.AppendLine(line[..^1]);
                prompt = continuationPrompt;
                continue;
            }

            sb.Append(line);
            var result = sb.ToString();
            if (!string.IsNullOrWhiteSpace(result))
            {
                AddToHistory(result);
            }
            return result;
        }

        return null;
    }

    public async Task<string?> ReadSingleLineAsync(
        TextReader inReader,
        TextWriter outWriter,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        // Safe fallback for non-interactive / redirected console or unit tests
        if (Console.IsInputRedirected || Console.IsOutputRedirected || inReader != Console.In)
        {
            await outWriter.WriteAsync(prompt).ConfigureAwait(false);
            await outWriter.FlushAsync().ConfigureAwait(false);
            return await inReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadInteractiveKeyLoopAsync(outWriter, prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadInteractiveKeyLoopAsync(
        TextWriter outWriter,
        string prompt,
        CancellationToken cancellationToken)
    {
        await outWriter.WriteAsync(prompt).ConfigureAwait(false);
        await outWriter.FlushAsync().ConfigureAwait(false);

        var buffer = new StringBuilder();
        int cursor = 0;
        int historyIndex = _history.Count;
        string tempInput = string.Empty;
        int promptLen = StripAnsi(prompt).Length;

        void RenderLine()
        {
            try
            {
                Console.CursorLeft = 0;
                outWriter.Write(prompt + buffer.ToString() + "   ");
                Console.CursorLeft = promptLen + cursor;
                outWriter.Flush();
            }
            catch
            {
                // Fallback if Console.CursorLeft throws
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                await outWriter.WriteLineAsync().ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                buffer.Clear();
                cursor = 0;
                RenderLine();
                continue;
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                await outWriter.WriteLineAsync("^C").ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                return string.Empty;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (cursor > 0)
                {
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                    RenderLine();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursor < buffer.Length)
                {
                    buffer.Remove(cursor, 1);
                    RenderLine();
                }
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0)
                {
                    cursor--;
                    try { Console.CursorLeft = promptLen + cursor; } catch { }
                }
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length)
                {
                    cursor++;
                    try { Console.CursorLeft = promptLen + cursor; } catch { }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                try { Console.CursorLeft = promptLen; } catch { }
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                try { Console.CursorLeft = promptLen + cursor; } catch { }
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (_history.Count > 0 && historyIndex > 0)
                {
                    if (historyIndex == _history.Count)
                    {
                        tempInput = buffer.ToString();
                    }
                    historyIndex--;
                    buffer.Clear().Append(_history[historyIndex]);
                    cursor = buffer.Length;
                    RenderLine();
                }
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (historyIndex < _history.Count)
                {
                    historyIndex++;
                    if (historyIndex == _history.Count)
                    {
                        buffer.Clear().Append(tempInput);
                    }
                    else
                    {
                        buffer.Clear().Append(_history[historyIndex]);
                    }
                    cursor = buffer.Length;
                    RenderLine();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var cur = buffer.ToString();
                var completion = AutoComplete(cur, DefaultSlashCommands);
                if (completion != null && !string.Equals(completion, cur, StringComparison.Ordinal))
                {
                    buffer.Clear().Append(completion).Append(' ');
                    cursor = buffer.Length;
                    RenderLine();
                }
                continue;
            }

            // Normal typing
            if (!char.IsControl(key.KeyChar))
            {
                buffer.Insert(cursor, key.KeyChar);
                cursor++;
                RenderLine();
            }
        }

        return null;
    }

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Regex.Replace(text, @"\x1b\[[0-9;]*[a-zA-Z]", "");
    }
}

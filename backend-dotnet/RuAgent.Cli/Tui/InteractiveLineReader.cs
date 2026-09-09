using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Cli.Tui;

public sealed class InteractiveLineReader
{
    private readonly List<string> _history = new();

    public sealed record SlashCommandInfo(string Command, string Description, string Syntax = "");

    public static readonly SlashCommandInfo[] KnownCommands =
    [
        new("/model", "Đổi hoặc xem mô hình LLM", "/model [name]"),
        new("/backend", "Chuyển backend (kilo, opencode, ollama...)", "/backend <name>"),
        new("/fork", "Rẽ nhánh phiên hội thoại hiện tại", "/fork [title]"),
        new("/tree", "Xem cây lịch sử phân nhánh (DAG)", "/tree"),
        new("/stats", "Xem thống kê tokens & phiên làm việc", "/stats"),
        new("/tokens", "Xem chi tiết token tiêu thụ", "/tokens"),
        new("/export", "Xuất lịch sử chat ra tệp Markdown", "/export [filename]"),
        new("/status", "Xem trạng thái session & preset", "/status"),
        new("/rewind", "Quay ngược lại tin nhắn trước", "/rewind <index>"),
        new("/history", "Xem lịch sử câu lệnh đã nhập", "/history"),
        new("/clear", "Xóa màn hình terminal", "/clear"),
        new("/help", "Xem danh sách hướng dẫn lệnh", "/help"),
        new("/exit", "Thoát chương trình", "/exit"),
        new("/quit", "Thoát chương trình", "/quit"),
    ];

    private static readonly string[] DefaultSlashCommands = KnownCommands.Select(c => c.Command).ToArray();

    public IReadOnlyList<string> History => _history;

    /// <summary>Legend gắn ở cạnh dưới composer box (vd "model · preset").</summary>
    public string Legend { get; set; } = string.Empty;

    public static IReadOnlyList<SlashCommandInfo> FindMatches(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/'))
        {
            return Array.Empty<SlashCommandInfo>();
        }

        var prefix = input.Trim();
        return KnownCommands
            .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string BuildTop(int width) => BuildTop(width, false);

    public static string BuildTop(int width, bool isSlash)
    {
        var w = Math.Clamp(width, 10, 200);
        var border = "╭" + new string('─', w - 2) + "╮";
        return isSlash ? AnsiTheme.Cyan(border) : AnsiTheme.Gray(border);
    }

    public static string BuildMid(int width, string prompt, string buffer) => BuildMid(width, prompt, buffer, "", false);

    public static string BuildMid(int width, string prompt, string buffer, bool isSlash) => BuildMid(width, prompt, buffer, "", isSlash);

    public static string BuildMid(int width, string prompt, string buffer, string ghostText, bool isSlash)
    {
        var w = Math.Clamp(width, 10, 200);
        var visibleLen = StripAnsi(prompt).Length + (buffer?.Length ?? 0) + (ghostText?.Length ?? 0);
        var pad = Math.Max(0, w - 2 - 2 - visibleLen);
        var left = isSlash ? AnsiTheme.Cyan("│ ") : AnsiTheme.Gray("│ ");
        var right = isSlash ? AnsiTheme.Cyan(" │") : AnsiTheme.Gray(" │");
        var ghost = string.IsNullOrEmpty(ghostText) ? "" : AnsiTheme.Gray(ghostText);
        return left + prompt + (buffer ?? string.Empty) + ghost + new string(' ', pad) + right;
    }

    public static string BuildBottom(int width, string legend) => BuildBottom(width, legend, false);

    public static string BuildBottom(int width, string legend, bool isSlash)
    {
        var w = Math.Clamp(width, 10, 200);
        var label = string.IsNullOrWhiteSpace(legend) ? "" : " " + legend.Trim() + " ";
        var fill = Math.Max(0, w - 2 - StripAnsi(label).Length);
        var border = isSlash
            ? AnsiTheme.Cyan("╰" + new string('─', fill)) + label + AnsiTheme.Cyan("╯")
            : AnsiTheme.Gray("╰" + new string('─', fill)) + label + AnsiTheme.Gray("╯");
        return border;
    }

    public static string BuildDropdownTop(int width)
    {
        var w = Math.Clamp(width, 24, 200);
        var title = " Gợi ý lệnh ";
        var fill = Math.Max(0, w - 2 - title.Length);
        return AnsiTheme.Cyan("╭" + title + new string('─', fill) + "╮");
    }

    public static string BuildDropdownRow(int width, SlashCommandInfo item, bool isSelected)
    {
        var w = Math.Clamp(width, 24, 200);
        var prefix = isSelected ? "❯ " : "  ";
        var cmd = item.Command.PadRight(11);
        var desc = item.Description;
        var syntax = string.IsNullOrWhiteSpace(item.Syntax) ? "" : " " + item.Syntax;

        var innerWidth = w - 4; // trừ 2 viền '│ ' và ' │'
        var textContent = prefix + cmd + " " + desc + syntax;
        if (textContent.Length > innerWidth)
        {
            textContent = prefix + cmd + " " + desc;
            if (textContent.Length > innerWidth)
            {
                textContent = textContent[..(innerWidth - 1)] + "…";
            }
        }
        var pad = Math.Max(0, innerWidth - textContent.Length);

        string innerStyled;
        if (isSelected)
        {
            innerStyled = AnsiTheme.Bold(AnsiTheme.Cyan(prefix + cmd)) + " " + AnsiTheme.White(desc) + AnsiTheme.Gray(syntax) + new string(' ', pad);
        }
        else
        {
            innerStyled = AnsiTheme.Cyan(prefix + cmd) + " " + AnsiTheme.Gray(desc + syntax) + new string(' ', pad);
        }

        var border = isSelected ? AnsiTheme.Cyan("│") : AnsiTheme.Gray("│");
        return border + " " + innerStyled + " " + border;
    }

    public static string BuildDropdownBottom(int width, int current, int total)
    {
        var w = Math.Clamp(width, 24, 200);
        var hint = $" [{current + 1}/{total}] ↑/↓: chọn · Tab: điền · Esc: đóng ";
        var fill = Math.Max(0, w - 2 - hint.Length);
        return AnsiTheme.Cyan("╰" + hint + new string('─', fill) + "╯");
    }

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", false);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, bool isSlash) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", isSlash);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, string ghostText, bool isSlash)
    {
        var mid = BuildMid(width, prompt, buffer ?? string.Empty, ghostText, isSlash);
        var col = 2 + StripAnsi(prompt).Length + Math.Max(0, cursor);
        return "\r" + mid + "\r\x1b[" + col + "C";
    }

    public static string BuildBoxOpen(int width, string prompt, string legend) => BuildBoxOpen(width, prompt, legend, false);

    public static string BuildBoxOpen(int width, string prompt, string legend, bool isSlash)
    {
        var nl = Environment.NewLine;
        return BuildTop(width, isSlash) + nl
            + (isSlash ? AnsiTheme.Cyan("│ ") : AnsiTheme.Gray("│ ")) + prompt + nl
            + BuildBottom(width, legend, isSlash) + nl
            + "\x1b[2A";
    }

    public static int ComputeBoxWidth()
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 22)
            {
                return Console.WindowWidth - 2;
            }
        }
        catch { }
        return 76;
    }

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
        var buffer = new StringBuilder();
        int cursor = 0;
        int historyIndex = _history.Count;
        string tempInput = string.Empty;
        const string boxPrompt = "";
        int boxWidth = ComputeBoxWidth();
        bool lastIsSlash = false;
        int selectedIndex = 0;
        string defaultLegend = Legend;
        int lastDropdownLines = 0;
        bool dropdownDismissed = false;

        // Dự trữ trước không gian bên dưới để terminal tự cuộn nếu ở sát đáy viewport
        for (int i = 0; i < 8; i++)
        {
            await outWriter.WriteLineAsync().ConfigureAwait(false);
        }
        await outWriter.WriteAsync("\x1b[8A").ConfigureAwait(false);

        await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, defaultLegend, false)).ConfigureAwait(false);
        await outWriter.FlushAsync().ConfigureAwait(false);

        (string ghost, string legend, IReadOnlyList<SlashCommandInfo> matches) ComputeAutocomplete()
        {
            var text = buffer.ToString();
            if (!text.StartsWith('/') || text.Contains(' '))
            {
                return ("", defaultLegend, Array.Empty<SlashCommandInfo>());
            }

            var matches = FindMatches(text);
            if (matches.Count == 0)
            {
                return ("", AnsiTheme.Gray("✦ Không tìm thấy lệnh phù hợp"), matches);
            }

            if (selectedIndex >= matches.Count) selectedIndex = 0;
            if (selectedIndex < 0) selectedIndex = matches.Count - 1;

            var chosen = matches[selectedIndex].Command;
            string ghost = "";
            if (cursor == text.Length && chosen.StartsWith(text, StringComparison.OrdinalIgnoreCase) && chosen.Length > text.Length)
            {
                ghost = chosen[text.Length..];
            }

            var sbLegend = new StringBuilder();
            sbLegend.Append(AnsiTheme.Cyan("✦ "));
            int displayCount = Math.Min(matches.Count, 4);
            for (int i = 0; i < displayCount; i++)
            {
                if (i > 0) sbLegend.Append(' ');
                if (i == selectedIndex)
                {
                    sbLegend.Append(AnsiTheme.Bold(AnsiTheme.Cyan($"[{matches[i].Command}]")));
                }
                else
                {
                    sbLegend.Append(AnsiTheme.Gray(matches[i].Command));
                }
            }
            if (matches.Count > displayCount)
            {
                sbLegend.Append(AnsiTheme.Gray($" +{matches.Count - displayCount}"));
            }
            sbLegend.Append(AnsiTheme.Dim(" (Tab: điền · ↑/↓: chọn)"));

            return (ghost, sbLegend.ToString(), matches);
        }

        void ClearDropdown()
        {
            if (lastDropdownLines > 0)
            {
                outWriter.Write("\x1b[2B");
                for (int i = 0; i < lastDropdownLines; i++)
                {
                    outWriter.Write("\r\x1b[2K");
                    if (i < lastDropdownLines - 1) outWriter.Write("\n");
                }
                outWriter.Write($"\x1b[{2 + lastDropdownLines - 1}A");
                lastDropdownLines = 0;
            }
        }

        void RenderDropdown(IReadOnlyList<SlashCommandInfo> matches, int selected)
        {
            if (matches.Count == 0 || dropdownDismissed)
            {
                ClearDropdown();
                return;
            }

            int maxItems = Math.Min(5, matches.Count);
            int startIndex = 0;
            if (matches.Count > maxItems)
            {
                startIndex = Math.Clamp(selected - maxItems / 2, 0, matches.Count - maxItems);
            }

            var lines = new List<string>();
            lines.Add(BuildDropdownTop(boxWidth));
            for (int i = 0; i < maxItems; i++)
            {
                int idx = startIndex + i;
                lines.Add(BuildDropdownRow(boxWidth, matches[idx], idx == selected));
            }
            lines.Add(BuildDropdownBottom(boxWidth, selected, matches.Count));

            if (lastDropdownLines > lines.Count)
            {
                ClearDropdown();
            }

            outWriter.Write("\x1b[2B");
            for (int i = 0; i < lines.Count; i++)
            {
                outWriter.Write($"\r\x1b[2K{lines[i]}");
                if (i < lines.Count - 1) outWriter.Write("\n");
            }
            outWriter.Write($"\x1b[{2 + lines.Count - 1}A");
            lastDropdownLines = lines.Count;
        }

        void Refresh()
        {
            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            try
            {
                if (isSlash != lastIsSlash)
                {
                    outWriter.Write($"\x1b[1A\r{BuildTop(boxWidth, isSlash)}");
                    outWriter.Write($"\x1b[1B\r{BuildMid(boxWidth, boxPrompt, text, ghost, isSlash)}");
                    outWriter.Write($"\x1b[1B\r{BuildBottom(boxWidth, leg, isSlash)}");
                    outWriter.Write("\x1b[1A");
                    lastIsSlash = isSlash;
                }
                else
                {
                    outWriter.Write($"\x1b[1B\r{BuildBottom(boxWidth, leg, isSlash)}\x1b[1A");
                }

                if (isSlash && !dropdownDismissed && matches.Count > 0)
                {
                    RenderDropdown(matches, selectedIndex);
                }
                else
                {
                    ClearDropdown();
                }

                outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash));
                outWriter.Flush();
            }
            catch { }
        }

        void LeaveBox()
        {
            try
            {
                ClearDropdown();
                outWriter.Write($"\x1b[1B\r{BuildBottom(boxWidth, defaultLegend, false)}\x1b[1B");
                outWriter.Flush();
            }
            catch { }
        }

        Refresh();

        while (!cancellationToken.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            var key = Console.ReadKey(intercept: true);
            var curText = buffer.ToString();
            bool isSlash = curText.StartsWith('/') && !curText.Contains(' ');
            var (_, _, currentMatches) = isSlash ? ComputeAutocomplete() : default;

            if (key.Key == ConsoleKey.Enter)
            {
                if (isSlash && !dropdownDismissed && currentMatches != null && currentMatches.Count > 0)
                {
                    var chosen = currentMatches[selectedIndex].Command;
                    if (!string.Equals(chosen, curText, StringComparison.OrdinalIgnoreCase))
                    {
                        buffer.Clear().Append(chosen);
                    }
                }
                ClearDropdown();
                LeaveBox();
                await outWriter.WriteLineAsync().ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                if (isSlash && lastDropdownLines > 0)
                {
                    dropdownDismissed = true;
                    ClearDropdown();
                    Refresh();
                    continue;
                }

                buffer.Clear();
                cursor = 0;
                selectedIndex = 0;
                dropdownDismissed = false;
                Refresh();
                continue;
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ClearDropdown();
                LeaveBox();
                await outWriter.WriteLineAsync("^C").ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                return string.Empty;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                dropdownDismissed = false;
                if (cursor > 0)
                {
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                    selectedIndex = 0;
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                dropdownDismissed = false;
                if (cursor < buffer.Length)
                {
                    buffer.Remove(cursor, 1);
                    selectedIndex = 0;
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0)
                {
                    cursor--;
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length)
                {
                    cursor++;
                    Refresh();
                }
                else if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    var chosen = currentMatches[selectedIndex].Command;
                    buffer.Clear().Append(chosen).Append(' ');
                    cursor = buffer.Length;
                    dropdownDismissed = true;
                    ClearDropdown();
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                Refresh();
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                Refresh();
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    selectedIndex = (selectedIndex - 1 + currentMatches.Count) % currentMatches.Count;
                    Refresh();
                    continue;
                }

                if (_history.Count > 0 && historyIndex > 0)
                {
                    if (historyIndex == _history.Count)
                    {
                        tempInput = buffer.ToString();
                    }
                    historyIndex--;
                    buffer.Clear().Append(_history[historyIndex]);
                    cursor = buffer.Length;
                    selectedIndex = 0;
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    selectedIndex = (selectedIndex + 1) % currentMatches.Count;
                    Refresh();
                    continue;
                }

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
                    selectedIndex = 0;
                    Refresh();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    var chosen = currentMatches[selectedIndex].Command;
                    buffer.Clear().Append(chosen).Append(' ');
                    cursor = buffer.Length;
                    selectedIndex = 0;
                    dropdownDismissed = true;
                    ClearDropdown();
                    Refresh();
                    continue;
                }

                var cur = buffer.ToString();
                var completion = AutoComplete(cur, DefaultSlashCommands);
                if (completion != null && !string.Equals(completion, cur, StringComparison.Ordinal))
                {
                    buffer.Clear().Append(completion).Append(' ');
                    cursor = buffer.Length;
                    dropdownDismissed = true;
                    ClearDropdown();
                    Refresh();
                }
                continue;
            }

            // Normal typing
            if (!char.IsControl(key.KeyChar))
            {
                dropdownDismissed = false;
                buffer.Insert(cursor, key.KeyChar);
                cursor++;
                selectedIndex = 0;
                Refresh();
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

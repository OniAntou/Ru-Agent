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

    private const int MinBoxWidth = 10;
    private const int MaxBoxWidth = 1000;
    private const int MinDropdownWidth = 24;
    private const int CommandColumnWidth = 11;
    private const int ResizeDebounceMs = 120;
    private const int KeyPollMs = 20;
    private const int DefaultBoxWidth = 80;
    private const int MinConsoleWidth = 10;
    private const int PushBottomRows = 4;
    private const int MinConsoleHeight = 5;
    private const int DefaultConsoleHeight = 24;

    public IReadOnlyList<string> History => _history;

    /// <summary>Legend gắn ở cạnh dưới composer box (vd "model · preset").</summary>
    public string Legend { get; set; } = string.Empty;

    /// <summary>
    /// Callback được gọi khi cửa sổ terminal thay đổi kích cỡ (resize) và đã debounce ổn định.
    /// Giúp re-render toàn bộ màn hình một cách sạch sẽ, tránh vỡ viền hoặc reflow rác.
    /// </summary>
    public Func<Task>? OnScreenRefreshedAsync { get; set; }

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
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        var border = "╭" + new string('─', w - 2) + "╮";
        return isSlash ? AnsiTheme.Cyan(border) : AnsiTheme.Gray(border);
    }

    public static string BuildMid(int width, string prompt, string buffer) => BuildMid(width, prompt, buffer, "", false);

    public static string BuildMid(int width, string prompt, string buffer, bool isSlash) => BuildMid(width, prompt, buffer, "", isSlash);

    public static string BuildMid(int width, string prompt, string buffer, string ghostText, bool isSlash)
    {
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        string promptClean = prompt ?? string.Empty;
        string text = buffer ?? string.Empty;
        string ghost = ghostText ?? string.Empty;

        int promptLen = StripAnsi(promptClean).Length;
        int innerWidth = Math.Max(1, w - 4); // trừ 2 viền "│ " và " │"
        int availableForText = Math.Max(1, innerWidth - promptLen);

        string visibleText = text;
        string visibleGhost = ghost;

        if (text.Length + ghost.Length > availableForText)
        {
            if (ghost.Length > 0)
            {
                int maxGhost = Math.Max(0, availableForText - text.Length);
                visibleGhost = maxGhost > 0 ? ghost[..Math.Min(ghost.Length, maxGhost)] : "";
            }

            if (text.Length + visibleGhost.Length > availableForText)
            {
                visibleGhost = "";
                int start = Math.Max(0, text.Length - availableForText);
                visibleText = text[start..];
            }
        }

        var visibleLen = promptLen + visibleText.Length + visibleGhost.Length;
        var pad = Math.Max(0, innerWidth - visibleLen);

        var left = isSlash ? AnsiTheme.Cyan("│ ") : AnsiTheme.Gray("│ ");
        var right = isSlash ? AnsiTheme.Cyan(" │") : AnsiTheme.Gray(" │");
        var styledGhost = string.IsNullOrEmpty(visibleGhost) ? "" : AnsiTheme.Gray(visibleGhost);

        return left + promptClean + visibleText + styledGhost + new string(' ', pad) + right;
    }

    public static string BuildBottom(int width, string legend) => BuildBottom(width, legend, false);

    public static string BuildBottom(int width, string legend, bool isSlash)
    {
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        var label = string.IsNullOrWhiteSpace(legend) ? "" : " " + legend.Trim() + " ";
        int maxLabelLen = Math.Max(0, w - 4);
        var plainLabel = StripAnsi(label);
        if (plainLabel.Length > maxLabelLen)
        {
            // Truncate by display width. A styled label degrades to plain text
            // here so an ANSI sequence is never cut in half.
            label = maxLabelLen <= 1 ? plainLabel[..maxLabelLen] : plainLabel[..(maxLabelLen - 1)] + "…";
        }
        var fill = Math.Max(0, w - 2 - StripAnsi(label).Length);
        var border = isSlash
            ? AnsiTheme.Cyan("╰" + new string('─', fill)) + label + AnsiTheme.Cyan("╯")
            : AnsiTheme.Gray("╰" + new string('─', fill)) + label + AnsiTheme.Gray("╯");
        return border;
    }

    public static string BuildDropdownTop(int width)
    {
        var w = Math.Clamp(width, MinDropdownWidth, MaxBoxWidth);
        var title = " Gợi ý lệnh ";
        var fill = Math.Max(0, w - 2 - title.Length);
        return AnsiTheme.Cyan("╭" + title + new string('─', fill) + "╮");
    }

    public static string BuildDropdownRow(int width, SlashCommandInfo item, bool isSelected)
    {
        var w = Math.Clamp(width, MinDropdownWidth, MaxBoxWidth);
        var prefix = isSelected ? "❯ " : "  ";
        var cmd = item.Command.PadRight(CommandColumnWidth);
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
        var w = Math.Clamp(width, MinDropdownWidth, MaxBoxWidth);
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
        var mid = BuildMid(width, prompt, buffer, ghostText, isSlash);
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        string text = buffer ?? string.Empty;
        int promptLen = StripAnsi(prompt ?? string.Empty).Length;
        int innerWidth = Math.Max(1, w - 4);
        int availableForText = Math.Max(1, innerWidth - promptLen);

        int displayCursor = cursor;
        if (text.Length > availableForText)
        {
            int start = Math.Max(0, text.Length - availableForText);
            displayCursor = Math.Clamp(cursor - start, 0, availableForText);
        }
        else
        {
            displayCursor = Math.Clamp(cursor, 0, availableForText);
        }

        int col = 2 + promptLen + displayCursor;
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
            if (!Console.IsOutputRedirected && Console.WindowWidth > MinConsoleWidth)
            {
                return Math.Max(MinBoxWidth, Console.WindowWidth - 2);
            }
        }
        catch (IOException)
        {
            // No live console (redirected/closed). Fall through to the default width.
        }
        return DefaultBoxWidth;
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

    public static async Task PushToBottomAsync(TextWriter outWriter)
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowHeight > MinConsoleHeight)
            {
                int winHeight = Console.WindowHeight;
                int cursorTop = Console.CursorTop;
                int targetRow = winHeight - PushBottomRows;
                if (cursorTop < targetRow && cursorTop > 0)
                {
                    int pushLines = targetRow - cursorTop;
                    for (int i = 0; i < pushLines; i++)
                    {
                        await outWriter.WriteLineAsync().ConfigureAwait(false);
                    }
                    await outWriter.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Console vanished mid-push. The box still renders at the current row.
        }
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

        await PushToBottomAsync(outWriter).ConfigureAwait(false);
        await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, defaultLegend, false)).ConfigureAwait(false);
        outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, "", 0, "", false));
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
                return ("", defaultLegend, matches);
            }

            if (selectedIndex >= matches.Count) selectedIndex = 0;
            if (selectedIndex < 0) selectedIndex = matches.Count - 1;

            var chosen = matches[selectedIndex].Command;
            string ghost = "";
            if (cursor == text.Length && chosen.StartsWith(text, StringComparison.OrdinalIgnoreCase) && chosen.Length > text.Length)
            {
                ghost = chosen[text.Length..];
            }

            return (ghost, defaultLegend, matches);
        }

        void ClearDropdown()
        {
            if (lastDropdownLines > 0)
            {
                outWriter.Write($"\x1b[{1 + lastDropdownLines}A");
                for (int i = 0; i < lastDropdownLines; i++)
                {
                    outWriter.Write("\r\x1b[2K\n");
                }
                outWriter.Write("\x1b[1B");
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

            outWriter.Write($"\x1b[{1 + lines.Count}A");
            for (int i = 0; i < lines.Count; i++)
            {
                outWriter.Write($"\r\x1b[2K{lines[i]}\n");
            }
            outWriter.Write("\x1b[1B");
            lastDropdownLines = lines.Count;
        }

        void RedrawBoxClean()
        {
            try
            {
                int upLines = 1 + lastDropdownLines;
                outWriter.Write($"\x1b[{upLines}A\r\x1b[J");
                outWriter.Flush();
            }
            catch (IOException)
            {
                // Output went away mid-redraw. Continue rebuilding state; next writes retry.
            }

            lastDropdownLines = 0;
            boxWidth = ComputeBoxWidth();

            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            outWriter.Write(BuildBoxOpen(boxWidth, boxPrompt, leg, isSlash));
            if (isSlash && !dropdownDismissed && matches.Count > 0)
            {
                RenderDropdown(matches, selectedIndex);
            }
            outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash));
            outWriter.Flush();
            lastIsSlash = isSlash;
        }

        void Refresh()
        {
            int curWidth = ComputeBoxWidth();
            if (curWidth != boxWidth)
            {
                RedrawBoxClean();
                return;
            }

            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            try
            {
                if (isSlash != lastIsSlash)
                {
                    outWriter.Write("\x1b[1A\r\x1b[2K" + BuildTop(boxWidth, isSlash));
                    outWriter.Write("\x1b[2B\r\x1b[2K" + BuildBottom(boxWidth, leg, isSlash));
                    outWriter.Write("\x1b[1A");
                    lastIsSlash = isSlash;
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
            catch (IOException)
            {
                // A failed frame must not kill the input loop; the next keypress redraws.
            }
        }

        void LeaveBox()
        {
            try
            {
                ClearDropdown();
                var text = buffer.ToString();
                outWriter.Write("\r\x1b[2K" + BuildMid(boxWidth, boxPrompt, text, "", false));
                outWriter.Write("\x1b[1B\r\x1b[2K" + BuildBottom(boxWidth, defaultLegend, false));
                outWriter.WriteLine();
                outWriter.Flush();
            }
            catch (IOException)
            {
                // Teardown best-effort only; the submitted line is already captured in buffer.
            }
        }

        Refresh();

        int lastWidth = ComputeBoxWidth();
        int lastHeight = Console.IsOutputRedirected ? DefaultConsoleHeight : Console.WindowHeight;
        bool resizePending = false;
        var resizeDebounceWatch = new System.Diagnostics.Stopwatch();

        async Task ApplyPendingResizeAsync()
        {
            resizePending = false;
            resizeDebounceWatch.Reset();

            try
            {
                int upLines = 1 + lastDropdownLines;
                outWriter.Write($"\x1b[{upLines}A\r\x1b[J");
                await outWriter.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Output went away mid-resize. Continue rebuilding state; next writes retry.
            }

            lastDropdownLines = 0;
            boxWidth = ComputeBoxWidth();

            if (OnScreenRefreshedAsync != null)
            {
                try
                {
                    await OnScreenRefreshedAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failing refresh callback must not kill the input loop.
                }
            }

            await PushToBottomAsync(outWriter).ConfigureAwait(false);

            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, leg, isSlash)).ConfigureAwait(false);
            if (isSlash && !dropdownDismissed && matches.Count > 0)
            {
                RenderDropdown(matches, selectedIndex);
            }
            outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash));
            await outWriter.FlushAsync().ConfigureAwait(false);
            lastIsSlash = isSlash;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested) return null;

                if (!Console.IsOutputRedirected)
                {
                    try
                    {
                        int curWidth = ComputeBoxWidth();
                        int curHeight = Console.WindowHeight;
                        if (curWidth != lastWidth || curHeight != lastHeight)
                        {
                            lastWidth = curWidth;
                            lastHeight = curHeight;
                            resizePending = true;
                            resizeDebounceWatch.Restart();
                        }
                        else if (resizePending && resizeDebounceWatch.ElapsedMilliseconds >= ResizeDebounceMs)
                        {
                            await ApplyPendingResizeAsync().ConfigureAwait(false);
                        }
                    }
                    catch (IOException)
                    {
                        // Console dimension probe failed mid-poll. Retry on the next tick.
                    }
                }

                await Task.Delay(KeyPollMs, cancellationToken).ConfigureAwait(false);
            }

            if (resizePending)
            {
                try
                {
                    await ApplyPendingResizeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Resize apply failed; the next keypress redraws from current state.
                }
            }

            var key = Console.ReadKey(intercept: true);
            var curText = buffer.ToString();
            bool isSlash = curText.StartsWith('/') && !curText.Contains(' ');
            var (_, _, currentMatches) = isSlash ? ComputeAutocomplete() : default;

            if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
            {
                if (buffer.Length == 0)
                {
                    continue;
                }

                if (isSlash && !dropdownDismissed && currentMatches != null && currentMatches.Count > 0)
                {
                    var chosen = currentMatches[selectedIndex].Command;
                    if (!string.Equals(chosen, curText, StringComparison.OrdinalIgnoreCase))
                    {
                        buffer.Clear().Append(chosen);
                    }
                }
                LeaveBox();
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

    public static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Regex.Replace(text, @"\x1b\[[0-9;]*[a-zA-Z]", "");
    }
}

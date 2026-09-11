using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Cli.Tui;

public sealed class InteractiveLineReader
{
    private readonly List<string> _history = new();

    public sealed record SlashCommandInfo(string Command, string Description, string Syntax = "");

    public static readonly SlashCommandInfo[] KnownCommands =
    [
        new("/model", "Switch or show the active model", "/model [name]"),
        new("/backend", "Switch provider backend (kilo, opencode, ollama...)", "/backend <name>"),
        new("/fork", "Branch this conversation", "/fork [title]"),
        new("/tree", "Show the session tree", "/tree"),
        new("/stats", "Show token and session stats", "/stats"),
        new("/tokens", "Show detailed token usage", "/tokens"),
        new("/export", "Export the transcript to Markdown", "/export [filename]"),
        new("/status", "Show session status and preset", "/status"),
        new("/rewind", "Rewind to an earlier message", "/rewind <index>"),
        new("/history", "Show entered prompt history", "/history"),
        new("/clear", "Clear the screen", "/clear"),
        new("/help", "Show every command", "/help"),
        new("/exit", "Quit", "/exit"),
        new("/quit", "Quit", "/quit"),
    ];

    private static readonly string[] DefaultSlashCommands = KnownCommands.Select(c => c.Command).ToArray();

    private const int MinBoxWidth = 10;
    private const int MaxBoxWidth = 1000;
    private const int MinDropdownWidth = 24;
    private const int CommandColumnWidth = 11;
    private const int ResizeDebounceMs = 250;
    private const int KeyPollMs = 20;
    private const int DefaultBoxWidth = 80;
    private const int MinConsoleWidth = 10;
    private const int PushBottomRows = 4;
    private const int MinConsoleHeight = 5;
    private const int DefaultConsoleHeight = 24;
    /// <summary>Số dòng trắng tối đa được đệm khi ghim khung nhập xuống đáy.
    /// Khoảng trống lớn hơn mức này thì giữ khung ngay dưới nội dung (màn hình trống không bị đẩy đáy).</summary>
    private const int MaxPushPadLines = 4;

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

    public static string BuildTop(int width, bool isSlash) =>
        ComposerBox.Compose(width, StateFor(isSlash), "│ ", string.Empty, string.Empty, string.Empty, string.Empty, null)[0];

    public static string BuildMid(int width, string prompt, string buffer) => BuildMid(width, prompt, buffer, "", false);

    public static string BuildMid(int width, string prompt, string buffer, bool isSlash) => BuildMid(width, prompt, buffer, "", isSlash);

    public static string BuildMid(int width, string prompt, string buffer, string ghostText, bool isSlash) =>
        ComposerBox.Compose(width, StateFor(isSlash), prompt, buffer, ghostText, string.Empty, string.Empty, null)[1];

    public static string BuildBottom(int width, string legend) => BuildBottom(width, legend, false);

    public static string BuildBottom(int width, string legend, bool isSlash) =>
        ComposerBox.Compose(width, StateFor(isSlash), "│ ", string.Empty, string.Empty, legend, string.Empty, null)[2];

    private static ComposerState StateFor(bool isSlash) => isSlash ? ComposerState.Slash : ComposerState.Idle;

    public static string BuildDropdownTop(int width) =>
        SlashDropdown.Compose(width, "commands", Array.Empty<DropdownItem>(), 0, 1, null)[0];

    public static string BuildDropdownRow(int width, SlashCommandInfo item, bool isSelected) =>
        SlashDropdown.Compose(
            width,
            "commands",
            [new DropdownItem(item.Command, item.Description, item.Syntax)],
            isSelected ? 0 : -1,
            1,
            null)[1];

    public static string BuildDropdownBottom(int width, int current, int total) =>
        SlashDropdown.Footer(width, "↑↓ select · tab apply · esc close", current + 1, total);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", false);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, bool isSlash) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", isSlash);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, string ghostText, bool isSlash)
    {
        var mid = BuildMid(width, prompt, buffer, ghostText, isSlash);
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        string text = buffer ?? string.Empty;
        int promptLen = TuiText.VisibleWidth(prompt);
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

    /// <summary>
    /// Submitted prompts echo as a single dim line: the transcript stays quiet and the
    /// three-row composer remains the only frame on screen.
    /// </summary>
    public static string BuildSubmitEcho(string text)
    {
        var body = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        return TuiPalette.DimPaint("  ❯ " + body);
    }

    public static string BuildBoxOpen(int width, string prompt, string legend, bool isSlash)
    {
        var newline = Environment.NewLine;
        var lines = ComposerBox.Compose(
            width,
            StateFor(isSlash),
            prompt,
            string.Empty,
            string.Empty,
            legend,
            string.Empty,
            null);
        return lines[0] + newline + lines[1] + newline + lines[2] + newline + "\x1b[2A";
    }

    public static int ComputeBoxWidth()
    {
        if (TuiViewport.TryReadLiveSize(out var columns, out _))
        {
            // Fit the live terminal: the box may never be wider than the screen it draws into.
            return Math.Clamp(TuiViewport.UsableWidth(columns, MinBoxWidth), 1, columns);
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

    /// <summary>
    /// Runs a picker under the composer using the shared selection behaviour. Returns the
    /// chosen value, or null when the operator cancelled — and also when stdin/stdout is not
    /// interactive, so callers fall back to a plain text listing.
    /// </summary>
    public async Task<string?> ReadChoiceAsync(
        ChoiceList list,
        TextWriter outWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(outWriter);
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !ReferenceEquals(outWriter, Console.Out))
        {
            return null;
        }

        return await ReadInteractiveKeyLoopAsync(outWriter, string.Empty, cancellationToken, list).ConfigureAwait(false);
    }

    /// <summary>
    /// Tính số dòng trắng cần đệm để ghim khung nhập xuống đáy viewport.
    /// Chỉ đệm khi output đã dài gần đầy màn hình (cách đáy không quá
    /// <c>MaxPushPadLines</c> dòng); màn hình còn trống thì trả 0 để khung
    /// nằm ngay dưới nội dung. Pure function nên unit test được.
    /// </summary>
    public static int ComputePushLines(int cursorTop, int windowHeight)
    {
        if (cursorTop <= 0) return 0;
        int pushLines = (windowHeight - PushBottomRows) - cursorTop;
        return pushLines is > 0 and <= MaxPushPadLines ? pushLines : 0;
    }

    public static async Task PushToBottomAsync(TextWriter outWriter)
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowHeight > MinConsoleHeight)
            {
                int pushLines = ComputePushLines(Console.CursorTop, Console.WindowHeight);
                for (int i = 0; i < pushLines; i++)
                {
                    await outWriter.WriteLineAsync().ConfigureAwait(false);
                }
                await outWriter.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Console vanished mid-push. The box still renders at the current row.
        }
    }

    /// <summary>
    /// Escape sequence that erases the composer and everything below it, ready for a rebuild.
    /// The hardware cursor normally rests on the frame's middle row, so one row up clears the
    /// whole box — but a terminal reflow can leave the frame a row lower than we think, which
    /// strands a piece of the old border on screen. When the cursor has room, the clear
    /// therefore reaches one row higher, into the blank row that always precedes the frame.
    /// Pure so the geometry can be tested without a console.
    /// </summary>
    public static string ComposeBoxClear(int cursorTop, int windowRows)
    {
        if (windowRows < 1)
        {
            return string.Empty;
        }

        // Rows 1 and 2 are the top of the screen: going up there would eat the header.
        var ups = cursorTop >= 3 ? 2 : 1;
        return $"\u001b[{ups}A\r\u001b[J";
    }

    private async Task<string?> ReadInteractiveKeyLoopAsync(
        TextWriter outWriter,
        string prompt,
        CancellationToken cancellationToken,
        ChoiceList? choice = null)
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

        // Picker mode: the list owns the keyboard and the composer is just its frame.
        bool choosing = choice is not null;
        if (choosing)
        {
            defaultLegend = choice!.Title;
        }

        // One blank row above every composer open: breathing room after the
        // header, transcript, or status bar. Pushed before PushToBottom so the
        // box itself lands in the same rows as before.
        await outWriter.WriteLineAsync().ConfigureAwait(false);
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
                // Dropdown lives below the box: from mid step past the bottom
                // border, clear each row moving down (no newlines: newline past
                // the viewport bottom would scroll), then step back to mid.
                outWriter.Write("\x1b[2B");
                for (int i = 0; i < lastDropdownLines; i++)
                {
                    outWriter.Write("\r\x1b[2K");
                    if (i < lastDropdownLines - 1)
                    {
                        outWriter.Write("\x1b[1B");
                    }
                }
                outWriter.Write($"\x1b[{lastDropdownLines + 1}A");
                lastDropdownLines = 0;
            }
        }

        void RenderBelow(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
            {
                ClearDropdown();
                return;
            }

            if (lastDropdownLines > lines.Count)
            {
                ClearDropdown();
            }

            // Draw below the box: step past the bottom border, write each row,
            // then step back to mid. Near the viewport bottom the terminal
            // scrolls (the list stays attached under the box) — accepted.
            outWriter.Write("\x1b[2B");
            for (int i = 0; i < lines.Count; i++)
            {
                outWriter.Write($"\r\x1b[2K{lines[i]}\n");
            }
            outWriter.Write($"\x1b[{lines.Count + 2}A");
            lastDropdownLines = lines.Count;
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

            var lines = new List<string>
            {
                BuildDropdownTop(boxWidth),
            };
            for (int i = 0; i < maxItems; i++)
            {
                int idx = startIndex + i;
                lines.Add(BuildDropdownRow(boxWidth, matches[idx], idx == selected));
            }
            lines.Add(BuildDropdownBottom(boxWidth, selected, matches.Count));

            RenderBelow(lines);
        }

        /// <summary>Renders whichever list is active: the picker, or the slash suggestions.</summary>
        void RenderCurrentList(bool isSlash, IReadOnlyList<SlashCommandInfo> matches)
        {
            if (choosing)
            {
                RenderBelow(choice!.Render(boxWidth));
                return;
            }

            if (isSlash && !dropdownDismissed && matches.Count > 0)
            {
                RenderDropdown(matches, selectedIndex);
            }
            else
            {
                ClearDropdown();
            }
        }

        void RedrawBoxClean()
        {
            try
            {
                // The clear reaches past the frame when there is room, so a reflowed frame
                // cannot leave a piece of the previous border behind (empty space below is
                // harmless: the rebuild repaints the blank row and the whole box).
                if (TuiViewport.TryReadLiveSize(out _, out var rows))
                {
                    outWriter.Write(ComposeBoxClear(Console.CursorTop, rows));
                    outWriter.WriteLine();
                }

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
            RenderCurrentList(isSlash, matches);
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

                RenderCurrentList(isSlash, matches);

                outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash));
                outWriter.Flush();
            }
            catch (IOException)
            {
                // A failed frame must not kill the input loop; the next keypress redraws.
            }
        }

        void LeaveBox(bool echo = true)
        {
            try
            {
                ClearDropdown();
                var text = buffer.ToString();
                // Xóa cả box 3 dòng (top/mid/bottom), in lại echo 1 dòng để không
                // bao giờ có 2 khung nhập giống nhau trên màn hình. Cursor đang ở
                // dòng mid: lên top xóa, xuống bottom xóa, in echo tại đó.
                outWriter.Write("\r\x1b[2K\x1b[1A\r\x1b[2K\x1b[2B\r\x1b[2K");
                if (echo)
                {
                    outWriter.Write(BuildSubmitEcho(text));
                }

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
        int appliedWidth = lastWidth;
        int appliedHeight = lastHeight;
        bool resizePending = false;
        var resizeDebounceWatch = new System.Diagnostics.Stopwatch();

        async Task ApplyPendingResizeAsync()
        {
            resizePending = false;
            resizeDebounceWatch.Reset();

            var settledWidth = ComputeBoxWidth();
            var settledHeight = Console.IsOutputRedirected ? DefaultConsoleHeight : Console.WindowHeight;
            if (settledWidth == appliedWidth && settledHeight == appliedHeight)
            {
                // The spam ended where the layout already is: reopening the frame and
                // rebuilding the screen would only flash identical content.
                return;
            }

            appliedWidth = settledWidth;
            appliedHeight = settledHeight;

            var cursorTop = Console.CursorTop;

            // A terminal that changed size has already reflowed every row this composer was
            // addressed at, so patching the frame cannot be reliable. The host rebuilds the
            // screen through this callback (region reset, clear, header, transcript tail); the
            // frame is then reopened at the cursor it leaves behind.
            lastDropdownLines = 0;
            boxWidth = ComputeBoxWidth();
            TuiDebug.Log(
                "composer",
                $"resize apply cursorTop={cursorTop} boxWidth={boxWidth} rows={TuiViewport.Height()}");

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

            // The rebuild below repaints in place from the top (clear, header,
            // transcript tail) and reopens the frame where it leaves the cursor:
            // pushing extra newlines here would scroll a copy of the screen into
            // the scrollback on every settled resize (see RedrawScreenForResize).
            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, leg, isSlash)).ConfigureAwait(false);
            RenderCurrentList(isSlash, matches);
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
                            TuiDebug.Log(
                                "composer",
                                $"resize detected {lastWidth}x{lastHeight} -> {curWidth}x{curHeight}");
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

            if (choosing)
            {
                switch (choice!.HandleKey(key))
                {
                    case ChoiceOutcome.Confirmed:
                        var picked = choice.Current.Value;
                        LeaveBox(echo: false);
                        return picked;

                    case ChoiceOutcome.Cancelled:
                        LeaveBox(echo: false);
                        return null;

                    default:
                        Refresh();
                        continue;
                }
            }

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

    /// <summary>
    /// Captures keystrokes while a turn streams. Enter hands the buffer to the one-slot queue,
    /// a pending approval owns the keyboard (arrows, digits, Enter, Escape), Escape cancels the
    /// turn, and Ctrl+O expands the last collapsed block. Every change repaints through
    /// <paramref name="repaint"/>; the loop returns only when the token is cancelled.
    /// </summary>
    /// <remarks>
    /// Terminal resizes are debounced through <see cref="TuiResizeWatcher"/>:
    /// <paramref name="onResize"/> runs once the window has settled, never during the drag,
    /// because repainting into a half-built window is what corrupts the pinned layout.
    /// </remarks>
    public async Task RunTypeAheadAsync(
        TypeAheadSession session,
        Action repaint,
        Action? onCancel = null,
        Action? onExpand = null,
        Action? onResize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repaint);

        // The first observation applies the layout for the viewport we start in.
        var resizeWatcher = new TuiResizeWatcher();
        if (TuiViewport.TryReadLiveSize(out var startColumns, out var startRows))
        {
            resizeWatcher.Observe(startColumns, startRows, Environment.TickCount64);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    HandleTypeAheadKey(session, Console.ReadKey(intercept: true), onCancel, onExpand);
                    repaint();
                    continue;
                }

                if (TuiViewport.TryReadLiveSize(out var columns, out var rows)
                    && resizeWatcher.Observe(columns, rows, Environment.TickCount64))
                {
                    onResize?.Invoke();
                    repaint();
                }
            }
            catch (IOException)
            {
                // Output vanished mid-frame; the next tick retries.
            }
            catch (InvalidOperationException)
            {
                // Input is no longer a live console (window closed mid-run).
                return;
            }

            await Task.Delay(KeyPollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Routes one keystroke for the pinned composer: a pending approval owns the keyboard,
    /// otherwise the buffer is edited and Enter queues. Exposed for direct unit testing.
    /// </summary>
    public static void HandleTypeAheadKey(
        TypeAheadSession session,
        ConsoleKeyInfo key,
        Action? onCancel = null,
        Action? onExpand = null)
    {
        if (session.Approval is { } approval)
        {
            // A pending approval owns the keyboard until it is answered.
            switch (approval.HandleKey(key))
            {
                case ChoiceOutcome.Confirmed:
                    approval.Resolve(approval.Current.Decision);
                    return;
                case ChoiceOutcome.Cancelled:
                    approval.Resolve(ApprovalPrompt.Deny);
                    return;
                default:
                    return;
            }
        }

        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
        {
            var text = session.Buffer.Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (session.Queue.TryQueue(text))
            {
                session.Buffer = string.Empty;
                session.QueueRejected = false;
            }
            else
            {
                session.QueueRejected = true;
            }

            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            // First Escape clears the draft, a second cancels the running turn.
            if (session.Buffer.Length > 0 || session.QueueRejected)
            {
                session.Buffer = string.Empty;
                session.QueueRejected = false;
                return;
            }

            onCancel?.Invoke();
            return;
        }

        bool control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (control && key.Key == ConsoleKey.O)
        {
            onExpand?.Invoke();
            return;
        }

        if (control && key.Key == ConsoleKey.C)
        {
            onCancel?.Invoke();
            return;
        }

        if (control && key.Key == ConsoleKey.U)
        {
            session.Buffer = string.Empty;
            session.QueueRejected = false;
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (session.Buffer.Length > 0)
            {
                session.Buffer = session.Buffer[..^1];
            }

            return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            session.QueueRejected = false;
            session.Buffer += key.KeyChar;
        }
    }
}

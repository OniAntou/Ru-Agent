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
        new("/key", "Show or update provider API keys", "/key [provider]"),
        new("/backend", "Switch provider backend (openai, anthropic, custom...)", "/backend <name>"),
        new("/reasoning", "Show or set the active model's reasoning effort", "/reasoning [effort]"),
        new("/tree", "Show the session tree", "/tree"),
        new("/stats", "Show token and session stats", "/stats"),
        new("/tokens", "Show detailed token usage", "/tokens"),
        new("/export", "Export the transcript to Markdown", "/export [filename]"),
        new("/status", "Show session status and preset", "/status"),
        new("/rewind", "Rewind to an earlier message", "/rewind <index>"),
        new("/continue", "Resume a parked run", "/continue [run-id]"),
        new("/delete", "Delete another session", "/delete [session-id]"),
        new("/history", "Show entered prompt history", "/history"),
        new("/clear", "Clear the screen", "/clear"),
        new("/help", "Show every command", "/help"),
        new("/exit", "Quit", "/exit"),
        new("/quit", "Quit", "/quit"),
    ];

    private static readonly string[] DefaultSlashCommands = KnownCommands.Select(c => c.Command).ToArray();

    private const int MinBoxWidth = 10;
    private const int MaxBoxWidth = 1000;
    /// <summary>Composer never spans the whole terminal: beyond this it centers like opencode.</summary>
    public const int ComposerMaxWidth = 100;
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
    public const string DefaultHints = "/ commands · ↑↓ history · tab complete · ctrl+o expand · ctrl+c stop";

    /// <summary>Ghost text shown while the composer is empty, mirroring the opencode ask box.</summary>
    public const string ComposerPlaceholder = "Ask anything, / for commands, /key for sign-in…";

    public string Legend { get; set; } = string.Empty;

    /// <summary>Top label displayed on the top border of the composer box (e.g. workspace path and preset).</summary>
    public string TopLabel { get; set; } = string.Empty;

    /// <summary>Keyboard shortcut hints rendered below the composer box.</summary>
    public string Hints { get; set; } = DefaultHints;

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

    public static string BuildTop(int width) => BuildTop(width, false, null);

    public static string BuildTop(int width, bool isSlash) => BuildTop(width, isSlash, null);

    public static string BuildTop(int width, bool isSlash, string? topLabel) =>
        BuildTop(width, isSlash, topLabel, 0);

    public static string BuildTop(int width, bool isSlash, string? topLabel, int leftMargin, bool surfaceBg = false)
    {
        var row = ComposerBox.Compose(width, StateFor(isSlash), "│ ", string.Empty, string.Empty, string.Empty, string.Empty, topLabel)[0];
        return Indent(surfaceBg ? WithSurfaceBg(row) : row, leftMargin);
    }

    public static string BuildMid(int width, string prompt, string buffer) => BuildMid(width, prompt, buffer, "", false);

    public static string BuildMid(int width, string prompt, string buffer, bool isSlash) => BuildMid(width, prompt, buffer, "", isSlash);

    public static string BuildMid(int width, string prompt, string buffer, string ghostText, bool isSlash) =>
        BuildMid(width, prompt, buffer, ghostText, isSlash, 0);

    public static string BuildMid(int width, string prompt, string buffer, string ghostText, bool isSlash, int leftMargin, bool surfaceBg = false)
    {
        var row = ComposerBox.Compose(width, StateFor(isSlash), prompt, buffer, ghostText, string.Empty, string.Empty, null)[1];
        return Indent(surfaceBg ? WithSurfaceBg(row) : row, leftMargin);
    }

    public static string BuildBottom(int width, string legend) => BuildBottom(width, legend, false);

    public static string BuildBottom(int width, string legend, bool isSlash) =>
        BuildBottom(width, legend, isSlash, 0);

    public static string BuildBottom(int width, string legend, bool isSlash, int leftMargin, bool surfaceBg = false)
    {
        var row = ComposerBox.Compose(width, StateFor(isSlash), "│ ", string.Empty, string.Empty, legend, string.Empty, null)[2];
        return Indent(surfaceBg ? WithSurfaceBg(row) : row, leftMargin);
    }

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

    /// <summary>
    /// Builds the ANSI escape sequence to render dropdown lines below the composer box.
    /// Steps 2 rows down from Mid (past bottom border to hints row), writes each line,
    /// and steps back up to Mid without trailing newlines that would cause spurious scrolling.
    /// </summary>
    public static string BuildDropdownRenderSequence(IReadOnlyList<string> lines) =>
        BuildDropdownRenderSequence(lines, 0);

    public static string BuildDropdownRenderSequence(IReadOnlyList<string> lines, int leftMargin)
    {
        if (lines == null || lines.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append("\x1b[2B");
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append("\r\x1b[2K").Append(Indent(lines[i], leftMargin));
            if (i < lines.Count - 1)
            {
                sb.Append('\n');
            }
        }
        sb.Append($"\r\x1b[{lines.Count + 1}A");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the ANSI escape sequence to clear dropdown lines and restore hints text below the composer box.
    /// Steps 2 rows down from Mid (to the hints row), restores hints text, clears all lower dropdown rows,
    /// and steps back up to Mid so the net vertical displacement is zero.
    /// </summary>
    public static string BuildDropdownClearSequence(int count, int boxWidth, string? hintsText = null) =>
        BuildDropdownClearSequence(count, boxWidth, hintsText, 0);

    public static string BuildDropdownClearSequence(int count, int boxWidth, string? hintsText, int leftMargin)
    {
        if (count <= 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append("\x1b[2B\r\x1b[2K");
        if (!string.IsNullOrWhiteSpace(hintsText))
        {
            var h = Indent(TuiPalette.Paint(TuiText.Truncate("  " + hintsText.Trim(), boxWidth), TuiToken.Line), leftMargin);
            sb.Append(h);
        }

        for (int i = 1; i < count; i++)
        {
            sb.Append("\x1b[1B\r\x1b[2K");
        }

        sb.Append($"\r\x1b[{count + 1}A");
        return sb.ToString();
    }

    /// <summary>
    /// Checks whether rendering dropdown lines below the composer box will exceed the viewport
    /// height and force the terminal to scroll.
    /// </summary>
    public static bool DidDropdownScroll(int cursorTop, int linesCount, int windowRows)
    {
        if (windowRows <= 0 || linesCount <= 0) return false;
        int available = windowRows - 2 - cursorTop;
        return linesCount > available;
    }

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", false);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, bool isSlash) =>
        BuildMidRewrite(width, prompt, buffer, cursor, "", isSlash);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, string ghostText, bool isSlash) =>
        BuildMidRewrite(width, prompt, buffer, cursor, ghostText, isSlash, 0);

    public static string BuildMidRewrite(int width, string prompt, string buffer, int cursor, string ghostText, bool isSlash, int leftMargin, bool surfaceBg = false)
    {
        var mid = BuildMid(width, prompt, buffer, ghostText, isSlash, leftMargin, surfaceBg);
        var w = Math.Clamp(width, MinBoxWidth, MaxBoxWidth);
        string text = buffer ?? string.Empty;
        int promptLen = TuiText.VisibleWidth(prompt);
        int innerWidth = Math.Max(1, w - 4);
        int availableForText = Math.Max(1, innerWidth - promptLen);
        int safeCursor = Math.Clamp(cursor, 0, text.Length);

        int displayCursor;
        if (TuiText.VisibleWidth(text) <= availableForText)
        {
            displayCursor = TuiText.VisibleWidth(text[..safeCursor]);
        }
        else
        {
            var shown = TuiText.Tail(text, availableForText);
            var tailStart = Math.Max(0, text.Length - Math.Max(0, shown.Length - 1));
            if (safeCursor <= tailStart)
            {
                displayCursor = 1;
            }
            else
            {
                displayCursor = 1 + TuiText.VisibleWidth(text[tailStart..safeCursor]);
            }
        }

        displayCursor = Math.Clamp(displayCursor, 0, availableForText);
        int col = 2 + promptLen + displayCursor + Math.Max(0, leftMargin);
        return "\r" + mid + "\r\x1b[" + col + "C";
    }

    public static string BuildBoxOpen(int width, string prompt, string legend) => BuildBoxOpen(width, prompt, legend, false, null, null);

    /// <summary>
    /// Submitted prompts echo cleanly in a modern elevated dark container card with a cyan chevron
    /// and embedded timestamp, matching modern CLI style (Claude Code / Cursor).
    /// Flexible across single-line and multiline inputs without rigid wireframe boxes or redundant labels.
    /// </summary>
    /// <summary>
    /// Formats the prompt row of the submit echo: a blue left border (OpenCode's user block),
    /// the elevated panel background, the prompt text and the right-aligned timestamp.
    /// </summary>
    public static string BuildPromptRow(string line, string timestamp, int width)
    {
        var w = width > 0 ? width : TuiViewport.Width();
        w = Math.Clamp(w, 20, 1000);

        var bg = TuiPalette.SurfaceBgCode();
        bool hasColor = !string.IsNullOrEmpty(bg) && TuiPalette.Level != TuiColorLevel.None;

        if (!hasColor)
        {
            return $"> {line} · {timestamp}";
        }

        var innerWidth = Math.Max(16, w - 1); // cells after the border glyph
        var textCode = TuiPalette.Code(TuiToken.Bright);
        var metaCode = TuiPalette.Code(TuiToken.Meta);
        var reset = TuiPalette.Reset;

        const int prefixWidth = 2;
        var timeWidth = TuiText.VisibleWidth(timestamp);
        var textWidth = TuiText.VisibleWidth(line);
        var shown = line;
        if (prefixWidth + textWidth + timeWidth + 3 > innerWidth)
        {
            shown = TuiText.Truncate(line, Math.Max(4, innerWidth - prefixWidth - timeWidth - 3));
        }

        var gap = Math.Max(1, innerWidth - prefixWidth - TuiText.VisibleWidth(shown) - timeWidth - 1);
        return TuiPalette.Paint("▌", TuiToken.Brand)
            + bg
            + "  "
            + textCode + shown + reset + bg
            + new string(' ', gap)
            + metaCode + timestamp + reset + bg
            + " "
            + reset;
    }

    /// <summary>
    /// Formats a continuation row of a multiline submit echo behind the same blue border.
    /// </summary>
    public static string BuildContinuationEcho(string text, int width)
    {
        var w = width > 0 ? width : TuiViewport.Width();
        w = Math.Clamp(w, 20, 1000);

        var bg = TuiPalette.SurfaceBgCode();
        bool hasColor = !string.IsNullOrEmpty(bg) && TuiPalette.Level != TuiColorLevel.None;

        if (!hasColor)
        {
            return "  " + text;
        }

        var innerWidth = Math.Max(16, w - 1);
        var available = Math.Max(1, innerWidth - 3);
        var shown = TuiText.VisibleWidth(text) > available ? TuiText.Truncate(text, available) : text;
        var pad = Math.Max(0, innerWidth - 3 - TuiText.VisibleWidth(shown));
        return TuiPalette.Paint("▌", TuiToken.Brand)
            + bg
            + "  "
            + TuiPalette.Code(TuiToken.Bright) + shown + TuiPalette.Reset + bg
            + new string(' ', pad)
            + " "
            + TuiPalette.Reset;
    }

    /// <summary>Blank padding row of the submit echo: border glyph plus panel background.</summary>
    public static string BuildEchoPadRow(int width)
    {
        var w = width > 0 ? width : TuiViewport.Width();
        w = Math.Clamp(w, 20, 1000);

        var bg = TuiPalette.SurfaceBgCode();
        bool hasColor = !string.IsNullOrEmpty(bg) && TuiPalette.Level != TuiColorLevel.None;
        if (!hasColor)
        {
            return string.Empty;
        }

        return TuiPalette.Paint("▌", TuiToken.Brand) + bg + new string(' ', Math.Max(0, w - 1)) + TuiPalette.Reset;
    }

    /// <summary>
    /// Formats the submitted prompt into an elevated container card with top/bottom padding
    /// matching the Claude Code terminal style.
    /// </summary>
    public static string BuildSubmitEcho(string text, string? time = null, int width = 0)
    {
        var timestamp = time ?? DateTime.Now.ToString("HH:mm");
        var clean = text?.Trim('\r', '\n') ?? string.Empty;
        var rawLines = clean.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (rawLines.Length == 0 || (rawLines.Length == 1 && string.IsNullOrWhiteSpace(rawLines[0])))
        {
            rawLines = [string.Empty];
        }

        var w = width > 0 ? width : TuiViewport.Width();
        w = Math.Clamp(w, 20, 1000);

        var bg = TuiPalette.SurfaceBgCode();
        bool hasColor = !string.IsNullOrEmpty(bg) && TuiPalette.Level != TuiColorLevel.None;

        if (!hasColor)
        {
            var monoLines = new List<string>();
            for (var i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i];
                if (i == 0)
                {
                    monoLines.Add($"> {line} · {timestamp}");
                }
                else
                {
                    monoLines.Add($"  {line}");
                }
            }
            return string.Join("\n", monoLines);
        }

        // OpenCode user block: padded panel rows around the bordered prompt rows.
        var lines = new List<string> { BuildEchoPadRow(w) };
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];
            if (i == 0)
            {
                lines.Add(BuildPromptRow(line, timestamp, w));
            }
            else
            {
                lines.Add(BuildContinuationEcho(line, w));
            }
        }
        lines.Add(BuildEchoPadRow(w));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Resizes a previously rendered submit echo line to match a new terminal width (e.g. on fullscreen/resize).
    /// If the line is not an echo card, it is safely clipped with TuiText.Truncate.
    /// </summary>
    public static string ResizeEchoLine(string line, int newWidth)
    {
        if (string.IsNullOrEmpty(line) || newWidth <= 0)
        {
            return line ?? string.Empty;
        }

        var bg = TuiPalette.SurfaceBgCode();
        if (string.IsNullOrEmpty(bg) || !line.Contains(bg, StringComparison.Ordinal))
        {
            return TuiText.Truncate(line, newWidth);
        }

        var stripped = TuiText.Strip(line);
        var trimmed = stripped.Trim();

        // Blank padding row: the border glyph with no content behind it.
        if (trimmed is "" or "▌")
        {
            return BuildEchoPadRow(newWidth);
        }

        // Prompt and continuation rows both start with the border glyph; only the first
        // row of the block carries the trailing timestamp.
        if (trimmed.StartsWith('▌'))
        {
            var body = trimmed[1..].Trim();
            if (body.Length == 0)
            {
                return BuildEchoPadRow(newWidth);
            }

            if (body.Length == 5
                && body[2] == ':'
                && char.IsDigit(body[0])
                && char.IsDigit(body[1])
                && char.IsDigit(body[3])
                && char.IsDigit(body[4]))
            {
                return BuildPromptRow(string.Empty, body, newWidth);
            }

            if (body.Length >= 6
                && body[^3] == ':'
                && char.IsDigit(body[^1])
                && char.IsDigit(body[^2])
                && char.IsDigit(body[^4])
                && char.IsDigit(body[^5]))
            {
                return BuildPromptRow(body[..^5].Trim(), body[^5..], newWidth);
            }

            return BuildContinuationEcho(body, newWidth);
        }

        return TuiText.Truncate(line, newWidth);
    }

    public static string BuildBoxOpen(int width, string prompt, string legend, bool isSlash) => BuildBoxOpen(width, prompt, legend, isSlash, null, null);

    public static string BuildBoxOpen(int width, string prompt, string legend, bool isSlash, string? topLabel, string? hints = null) =>
        BuildBoxOpen(width, prompt, legend, isSlash, topLabel, hints, 0);

    public static string BuildBoxOpen(int width, string prompt, string legend, bool isSlash, string? topLabel, string? hints, int leftMargin, bool surfaceBg = false)
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
            topLabel);

        string BoxRow(string row) => Indent(surfaceBg ? WithSurfaceBg(row) : row, leftMargin);
        var hintsLine = !string.IsNullOrWhiteSpace(hints)
            ? Indent(TuiPalette.Paint(TuiText.Truncate("  " + hints.Trim(), width), TuiToken.Line), leftMargin)
            : string.Empty;

        if (hintsLine.Length > 0)
        {
            return BoxRow(lines[0]) + newline + BoxRow(lines[1]) + newline + BoxRow(lines[2]) + newline + hintsLine + "\x1b[2A";
        }

        return BoxRow(lines[0]) + newline + BoxRow(lines[1]) + newline + BoxRow(lines[2]) + "\x1b[1A";
    }

    public static int ComputeBoxWidth()
    {
        if (TuiViewport.TryReadLiveSize(out var columns, out _))
        {
            // Fit the live terminal: the box may never be wider than the screen it draws into,
            // and never wider than the centered card.
            return Math.Clamp(Math.Min(TuiViewport.UsableWidth(columns, MinBoxWidth), ComposerMaxWidth), 1, columns);
        }

        return Math.Min(DefaultBoxWidth, ComposerMaxWidth);
    }

    /// <summary>Left margin that centers the box; 0 when there is no room to center.</summary>
    public static int ComputeBoxLeft(int viewportWidth, int boxWidth) =>
        viewportWidth <= boxWidth ? 0 : (viewportWidth - boxWidth) / 2;

    /// <summary>Left margin for the current viewport/box pair; 0 without a live console.</summary>
    public static int ComputeBoxLeftMargin()
    {
        if (TuiViewport.TryReadLiveSize(out var columns, out _))
        {
            return ComputeBoxLeft(columns, ComputeBoxWidth());
        }

        return 0;
    }

    private static string Indent(string line, int leftMargin) =>
        leftMargin <= 0 ? line : new string(' ', leftMargin) + line;

    /// <summary>
    /// Wraps one composer row in the elevated surface background. Inner resets are
    /// re-armed so the fill has no gaps; without colour support it returns the row as-is.
    /// </summary>
    public static string WithSurfaceBg(string row)
    {
        var bg = TuiPalette.SurfaceBgCode();
        if (string.IsNullOrEmpty(bg))
        {
            return row;
        }

        return bg + row.Replace(TuiPalette.Reset, TuiPalette.Reset + bg, StringComparison.Ordinal) + TuiPalette.Reset;
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

    private const int MaxSecretChars = 4096;

    /// <summary>
    /// Reads one secret without echo, history, or completion. Returns the typed value,
    /// or null when the operator cancelled (Escape) or the console is not interactive.
    /// Control characters are ignored; input is capped so a paste cannot grow it.
    /// Ctrl+V pastes from the Windows clipboard, because raw console input never
    /// triggers the terminal's own paste binding.
    /// </summary>
    public async Task<string?> ReadSecretAsync(
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
                var pasted = SanitizePastedText(ReadClipboardText(), MaxSecretChars - sb.Length);
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
        return sb.ToString();
    }

    /// <summary>
    /// Masked secret input inside a framed panel: the provider name on top, one
    /// bullet per entered character, hints at the bottom. The mask row is repainted
    /// in place per keystroke; nothing entered ever reaches the output. Returns the
    /// typed value, or null on Escape, cancellation, or a non-interactive console.
    /// </summary>
    public async Task<string?> ReadSecretPanelAsync(
        TextWriter outWriter,
        string providerDisplay,
        int width,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outWriter);
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !ReferenceEquals(outWriter, Console.Out))
        {
            return null;
        }

        foreach (var line in KeyInputPanel.Compose(width, providerDisplay, 0))
        {
            await outWriter.WriteLineAsync(line).ConfigureAwait(false);
        }

        await outWriter.FlushAsync().ConfigureAwait(false);

        var sb = new StringBuilder();
        var canRepaint = true;
        while (!cancellationToken.IsCancellationRequested)
        {
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
                    RepaintMaskRow(outWriter, width, sb.Length, ref canRepaint);
                }

                continue;
            }

            string append = string.Empty;
            if ((key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                || key.KeyChar == '\x16')
            {
                append = SanitizePastedText(ReadClipboardText(), MaxSecretChars - sb.Length);
            }
            else if (!char.IsControl(key.KeyChar) && sb.Length < MaxSecretChars)
            {
                append = key.KeyChar.ToString();
            }

            if (append.Length > 0)
            {
                sb.Append(append);
                RepaintMaskRow(outWriter, width, sb.Length, ref canRepaint);
            }
        }

        await outWriter.WriteLineAsync().ConfigureAwait(false);
        return sb.ToString();
    }

    private static void RepaintMaskRow(TextWriter outWriter, int width, int bulletCount, ref bool canRepaint)
    {
        if (!canRepaint)
        {
            return;
        }

        try
        {
            outWriter.Write("\u001b[2A\r\u001b[2K");
            outWriter.Write(KeyInputPanel.ComposeMaskRow(width, bulletCount));
            outWriter.Write("\u001b[2B");
            outWriter.Flush();
        }
        catch (IOException)
        {
            canRepaint = false;
        }
    }

    /// <summary>
    /// Keeps the first line of pasted text, trimmed and capped. Clipboard copies
    /// often trail a newline or spaces that would otherwise silently poison a key.
    /// Pure so it can be tested without a console.
    /// </summary>
    public static string SanitizePastedText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        var line = text.Split(['\r', '\n'], 2, StringSplitOptions.None)[0].Trim();
        return line.Length <= maxChars ? line : line[..maxChars];
    }

    private static string? ReadClipboardText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? result = null;
        try
        {
            var thread = new Thread(() => { result = ReadClipboardTextSta(); })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            return null;
        }

        return result;
    }

    private static string? ReadClipboardTextSta()
    {
        const uint CF_UNICODETEXT = 13;
        try
        {
            if (!NativeClipboard.IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                return null;
            }

            // The clipboard is often held open briefly by another app; retry
            // instead of reporting an empty paste on the first collision.
            var opened = false;
            for (var attempt = 0; attempt < 5 && !opened; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(80);
                }

                opened = NativeClipboard.OpenClipboard(IntPtr.Zero);
            }

            if (!opened)
            {
                return null;
            }

            try
            {
                var handle = NativeClipboard.GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var locked = NativeClipboard.GlobalLock(handle);
                if (locked == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(locked);
                }
                finally
                {
                    NativeClipboard.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeClipboard.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static class NativeClipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint uFormat);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);
    }

    /// <summary>
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
        int boxLeft = ComputeBoxLeftMargin();
        bool lastIsSlash = false;
        int selectedIndex = 0;
        string defaultLegend = Legend;
        string topLabelText = TopLabel;
        string hintsText = Hints;
        int lastDropdownLines = 0;
        bool dropdownDismissed = false;
        bool scrolledForDropdown = false;

        // Picker mode: the list owns the keyboard and the composer is just its frame.
        bool choosing = choice is not null;
        if (choosing)
        {
            defaultLegend = choice!.Title;
            topLabelText = string.Empty;
            hintsText = "↑↓ select · enter confirm · esc cancel";
        }

        // One blank row above every composer open: breathing room after the
        // header, transcript, or status bar. Pushed before PushToBottom so the
        // box itself lands in the same rows as before.
        await outWriter.WriteLineAsync().ConfigureAwait(false);
        await PushToBottomAsync(outWriter).ConfigureAwait(false);
        await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, defaultLegend, false, topLabelText, hintsText, boxLeft, true)).ConfigureAwait(false);
        outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, "", 0, "", false, boxLeft, true));
        await outWriter.FlushAsync().ConfigureAwait(false);

        (string ghost, string legend, IReadOnlyList<SlashCommandInfo> matches) ComputeAutocomplete()
        {
            var text = buffer.ToString();
            if (text.Length == 0 && !choosing)
            {
                return (ComposerPlaceholder, defaultLegend, Array.Empty<SlashCommandInfo>());
            }

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
                outWriter.Write(BuildDropdownClearSequence(lastDropdownLines, boxWidth, hintsText, boxLeft));
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

            if (!Console.IsOutputRedirected && TuiViewport.TryReadLiveSize(out _, out var rows))
            {
                if (DidDropdownScroll(Console.CursorTop, lines.Count, rows))
                {
                    scrolledForDropdown = true;
                }
            }

            outWriter.Write(BuildDropdownRenderSequence(lines, boxLeft));
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
            scrolledForDropdown = false;
            boxWidth = ComputeBoxWidth();
            boxLeft = ComputeBoxLeftMargin();

            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            outWriter.Write(BuildBoxOpen(boxWidth, boxPrompt, leg, isSlash, topLabelText, hintsText, boxLeft, true));
            RenderCurrentList(isSlash, matches);
            outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash, boxLeft, true));
            outWriter.Flush();
            lastIsSlash = isSlash;
        }

        async Task RefreshAsync()
        {
            int curWidth = ComputeBoxWidth();
            if (curWidth != boxWidth)
            {
                RedrawBoxClean();
                return;
            }

            boxLeft = ComputeBoxLeftMargin();

            var text = buffer.ToString();
            bool isSlash = text.StartsWith('/') && !text.Contains(' ');
            var (ghost, leg, matches) = ComputeAutocomplete();

            if (scrolledForDropdown && !choosing && (!isSlash || dropdownDismissed || matches.Count == 0))
            {
                ClearDropdown();
                scrolledForDropdown = false;
                await ApplyPendingResizeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                if (isSlash != lastIsSlash)
                {
                    outWriter.Write("\x1b[1A\r\x1b[2K" + BuildTop(boxWidth, isSlash, topLabelText, boxLeft, true));
                    outWriter.Write("\x1b[2B\r\x1b[2K" + BuildBottom(boxWidth, leg, isSlash, boxLeft, true));
                    outWriter.Write("\x1b[1A");
                    lastIsSlash = isSlash;
                }

                RenderCurrentList(isSlash, matches);

                outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash, boxLeft, true));
                outWriter.Flush();
            }
            catch (IOException)
            {
                // A failed frame must not kill the input loop; the next keypress redraws.
            }
        }

        void LeaveBox(bool echo = false)
        {
            try
            {
                ClearDropdown();
                var text = buffer.ToString();
                // Xóa cả box 3 dòng (top/mid/bottom) và dòng gợi ý phím tắt bên dưới (nếu có).
                // Cursor đang ở dòng mid: lên top xóa, xuống bottom xóa, xóa hints (nếu có),
                // quay lại top để giải phóng toàn bộ không gian của composer.
                outWriter.Write("\r\x1b[2K\x1b[1A\r\x1b[2K\x1b[2B\r\x1b[2K");
                if (!string.IsNullOrWhiteSpace(hintsText))
                {
                    outWriter.Write("\x1b[1B\r\x1b[2K\x1b[1A");
                }
                outWriter.Write("\x1b[2A\r");
                if (echo)
                {
                    outWriter.WriteLine(BuildSubmitEcho(text, width: boxWidth));
                }

                outWriter.Flush();
            }
            catch (IOException)
            {
                // Teardown best-effort only; the submitted line is already captured in buffer.
            }
        }

        await RefreshAsync().ConfigureAwait(false);

        var resizeWatcher = new TuiResizeWatcher(ResizeDebounceMs);
        if (TuiViewport.TryReadLiveSize(out var initCols, out var initRows))
        {
            resizeWatcher.Observe(initCols, initRows, Environment.TickCount64);
        }

        async Task ApplyPendingResizeAsync()
        {
            var cursorTop = Console.CursorTop;

            // A terminal that changed size has already reflowed every row this composer was
            // addressed at, so patching the frame cannot be reliable. The host rebuilds the
            // screen through this callback (region reset, clear, header, transcript tail); the
            // frame is then reopened at the cursor it leaves behind.
            lastDropdownLines = 0;
            scrolledForDropdown = false;
            boxWidth = ComputeBoxWidth();
            boxLeft = ComputeBoxLeftMargin();
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

            await outWriter.WriteAsync(BuildBoxOpen(boxWidth, boxPrompt, leg, isSlash, topLabelText, hintsText, boxLeft, true)).ConfigureAwait(false);
            RenderCurrentList(isSlash, matches);
            outWriter.Write(BuildMidRewrite(boxWidth, boxPrompt, text, cursor, ghost, isSlash, boxLeft, true));
            await outWriter.FlushAsync().ConfigureAwait(false);
            lastIsSlash = isSlash;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    LeaveBox(echo: false);
                    return null;
                }

                if (!Console.IsOutputRedirected && TuiViewport.TryReadLiveSize(out var curCols, out var curRows))
                {
                    if (resizeWatcher.Observe(curCols, curRows, Environment.TickCount64))
                    {
                        await ApplyPendingResizeAsync().ConfigureAwait(false);
                    }
                }

                try
                {
                    await Task.Delay(KeyPollMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LeaveBox(echo: false);
                    return null;
                }
            }

            var key = Console.ReadKey(intercept: true);
            var curText = buffer.ToString();
            bool isSlash = curText.StartsWith('/') && !curText.Contains(' ');
            var (_, _, currentMatches) = isSlash ? ComputeAutocomplete() : default;

            if (choosing)
            {
                var picker = choice!;
                if (key.Key == ConsoleKey.Backspace && picker.TryTrimFilter())
                {
                    await RefreshAsync().ConfigureAwait(false);
                    continue;
                }

                if (key.Key != ConsoleKey.Backspace && picker.TryAppendFilter(key.KeyChar))
                {
                    await RefreshAsync().ConfigureAwait(false);
                    continue;
                }

                switch (picker.HandleKey(key))
                {
                    case ChoiceOutcome.Confirmed:
                        var picked = picker.Current.Value;
                        LeaveBox(echo: false);
                        return picked;

                    case ChoiceOutcome.Cancelled:
                        LeaveBox(echo: false);
                        return null;

                    default:
                        await RefreshAsync().ConfigureAwait(false);
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
                LeaveBox(echo: false);
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                if (isSlash && lastDropdownLines > 0)
                {
                    dropdownDismissed = true;
                    ClearDropdown();
                    await RefreshAsync().ConfigureAwait(false);
                    continue;
                }

                buffer.Clear();
                cursor = 0;
                selectedIndex = 0;
                dropdownDismissed = false;
                await RefreshAsync().ConfigureAwait(false);
                continue;
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ClearDropdown();
                LeaveBox(echo: false);
                await outWriter.WriteLineAsync("^C").ConfigureAwait(false);
                await outWriter.FlushAsync().ConfigureAwait(false);
                return string.Empty;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                dropdownDismissed = false;
                if (cursor > 0)
                {
                    var deleteCount = cursor >= 2 && char.IsSurrogatePair(buffer[cursor - 2], buffer[cursor - 1]) ? 2 : 1;
                    buffer.Remove(cursor - deleteCount, deleteCount);
                    cursor -= deleteCount;
                    selectedIndex = 0;
                    await RefreshAsync().ConfigureAwait(false);
                }
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                dropdownDismissed = false;
                if (cursor < buffer.Length)
                {
                    var deleteCount = cursor + 1 < buffer.Length && char.IsSurrogatePair(buffer[cursor], buffer[cursor + 1]) ? 2 : 1;
                    buffer.Remove(cursor, deleteCount);
                    selectedIndex = 0;
                    await RefreshAsync().ConfigureAwait(false);
                }
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0)
                {
                    var step = cursor >= 2 && char.IsSurrogatePair(buffer[cursor - 2], buffer[cursor - 1]) ? 2 : 1;
                    cursor -= step;
                    await RefreshAsync().ConfigureAwait(false);
                }
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length)
                {
                    var step = cursor + 1 < buffer.Length && char.IsSurrogatePair(buffer[cursor], buffer[cursor + 1]) ? 2 : 1;
                    cursor += step;
                    await RefreshAsync().ConfigureAwait(false);
                }
                else if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    var chosen = currentMatches[selectedIndex].Command;
                    buffer.Clear().Append(chosen).Append(' ');
                    cursor = buffer.Length;
                    dropdownDismissed = true;
                    ClearDropdown();
                    await RefreshAsync().ConfigureAwait(false);
                }
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                await RefreshAsync().ConfigureAwait(false);
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                await RefreshAsync().ConfigureAwait(false);
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    selectedIndex = (selectedIndex - 1 + currentMatches.Count) % currentMatches.Count;
                    await RefreshAsync().ConfigureAwait(false);
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
                    await RefreshAsync().ConfigureAwait(false);
                }
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (isSlash && currentMatches != null && currentMatches.Count > 0)
                {
                    selectedIndex = (selectedIndex + 1) % currentMatches.Count;
                    await RefreshAsync().ConfigureAwait(false);
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
                    await RefreshAsync().ConfigureAwait(false);
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
                    await RefreshAsync().ConfigureAwait(false);
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
                    await RefreshAsync().ConfigureAwait(false);
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
                await RefreshAsync().ConfigureAwait(false);
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

        if (session.Choice is { } choice)
        {
            // A picker panel owns the keyboard: letters filter, digits quick-pick.
            if (key.Key == ConsoleKey.Backspace && choice.List.TryTrimFilter())
            {
                return;
            }

            if (key.Key != ConsoleKey.Backspace && choice.List.TryAppendFilter(key.KeyChar))
            {
                return;
            }

            switch (choice.List.HandleKey(key))
            {
                case ChoiceOutcome.Confirmed:
                    choice.Resolve(choice.List.Current.Value);
                    return;
                case ChoiceOutcome.Cancelled:
                    choice.Resolve(null);
                    return;
                default:
                    return;
            }
        }

        if (session.Secret is { } secret)
        {
            // Masked secret input: content never reaches the buffer editor, the history or
            // the transcript. Ctrl+V pastes from the clipboard because raw console input
            // never triggers the terminal's own paste binding.
            if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
            {
                secret.Resolve(secret.Buffer.ToString());
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                secret.Resolve(null);
                return;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Buffer.Length > 0)
                {
                    secret.Buffer.Length--;
                }

                return;
            }

            if ((key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                || key.KeyChar == '\x16')
            {
                var pasted = SanitizePastedText(ReadClipboardText(), PendingSecretRequest.MaxChars - secret.Buffer.Length);
                if (pasted.Length > 0)
                {
                    secret.Buffer.Append(pasted);
                }

                return;
            }

            if (!char.IsControl(key.KeyChar) && secret.Buffer.Length < PendingSecretRequest.MaxChars)
            {
                secret.Buffer.Append(key.KeyChar);
            }

            return;
        }

        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
        {
            var text = session.Buffer.Trim();
            if (text.Length == 0)
            {
                return;
            }

            // Between turns the composer is the REPL's prompt: Enter hands the line to the
            // idle reader. While a run streams (or a slash command runs) it queues instead.
            if (!session.TurnActive && session.IdleLine is { } idle)
            {
                session.Buffer = string.Empty;
                session.QueueRejected = false;
                idle.TrySetResult(text);
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
                var deleteCount = session.Buffer.Length >= 2 && char.IsSurrogatePair(session.Buffer[^2], session.Buffer[^1]) ? 2 : 1;
                session.Buffer = session.Buffer[..^deleteCount];
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

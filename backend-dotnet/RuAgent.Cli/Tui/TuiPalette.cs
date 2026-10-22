namespace RuAgent.Cli.Tui;

/// <summary>Semantic colour roles. Prose stays <see cref="Ink"/> (terminal default).</summary>
public enum TuiToken
{
    Brand,
    Brand2,
    Aurora,
    AuroraDim,
    Ink,
    Meta,
    Line,
    Ok,
    Warn,
    Err,
    Bright,
}

public enum TuiColorLevel
{
    None,
    Basic16,
    Palette256,
    TrueColor,
}

/// <summary>
/// The terminal palette cloned from OpenCode's dark theme: a steel-blue identity,
/// violet accents and warm neutrals. Colour depth degrades truecolor -> 256 -> 16 -> none.
/// </summary>
public static class TuiPalette
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";

    private static TuiColorLevel? _override;
    private static TuiColorLevel? _detected;

    /// <summary>Active colour level. Assignable so tests and the CLI can pin a depth.</summary>
    public static TuiColorLevel Level
    {
        // Detection reads seven environment variables plus Console.IsOutputRedirected and
        // used to run again for every painted fragment (per word of inline markdown, per
        // row of the live line). One process, one console: cache it.
        get => _override ?? (_detected ??= Detect());
        set => _override = value;
    }

    public static void ResetLevel()
    {
        _override = null;
        _detected = null;
    }

    /// <summary>
    /// Pins the level for the duration of a scope and restores the previous
    /// value on dispose, so parallel tests cannot leak a pinned depth into
    /// each other through the shared static.
    /// </summary>
    public static IDisposable PinLevel(TuiColorLevel level)
    {
        var previous = _override;
        _override = level;
        return new LevelScope(previous);
    }

    private sealed class LevelScope(TuiColorLevel? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _override = previous;
        }
    }

    public static TuiColorLevel Detect() => DetectFrom(
        Environment.GetEnvironmentVariable("RU_TUI_COLOR"),
        Environment.GetEnvironmentVariable("NO_COLOR"),
        Console.IsOutputRedirected,
        Environment.GetEnvironmentVariable("TERM"),
        Environment.GetEnvironmentVariable("COLORTERM"),
        Environment.GetEnvironmentVariable("WT_SESSION"),
        Environment.GetEnvironmentVariable("TERM_PROGRAM"));

    /// <summary>Pure depth resolution so the CLI can be tested without touching the environment.</summary>
    public static TuiColorLevel DetectFrom(
        string? requested,
        string? noColor,
        bool outputRedirected,
        string? term,
        string? colorTerm,
        string? windowsTerminalSession,
        string? termProgram)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            switch (requested.Trim().ToLowerInvariant())
            {
                case "truecolor":
                case "24bit":
                    return TuiColorLevel.TrueColor;
                case "256":
                    return TuiColorLevel.Palette256;
                case "16":
                    return TuiColorLevel.Basic16;
                case "none":
                    return TuiColorLevel.None;
            }
        }

        if (noColor is not null || outputRedirected)
        {
            return TuiColorLevel.None;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return TuiColorLevel.None;
        }

        if ((colorTerm is not null
                && (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
                    || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase)))
            || windowsTerminalSession is not null
            || (term is not null && term.Contains("direct", StringComparison.OrdinalIgnoreCase))
            || IsTrueColorProgram(termProgram))
        {
            return TuiColorLevel.TrueColor;
        }

        return TuiColorLevel.Palette256;
    }

    private static bool IsTrueColorProgram(string? program) =>
        program is not null
        && (program.Equals("WezTerm", StringComparison.OrdinalIgnoreCase)
            || program.Equals("iTerm.app", StringComparison.OrdinalIgnoreCase)
            || program.Equals("vscode", StringComparison.OrdinalIgnoreCase)
            || program.Equals("ghostty", StringComparison.OrdinalIgnoreCase)
            || program.Equals("Apple_Terminal", StringComparison.OrdinalIgnoreCase));

    /// <summary>Foreground escape sequence for a token, empty when colour is off or unset (ink).</summary>
    public static string Code(TuiToken token)
    {
        if (token == TuiToken.Ink)
        {
            return string.Empty;
        }

        return Level switch
        {
            TuiColorLevel.TrueColor => $"\u001b[38;2;{TrueColorParts(token)}m",
            TuiColorLevel.Palette256 => $"\u001b[38;5;{Palette256Index(token)}m",
            TuiColorLevel.Basic16 => $"\u001b[{(Basic16Index(token) >= 8 ? 9 : 3)}{Basic16Index(token) % 8}m",
            _ => string.Empty,
        };
    }

    public static string Paint(string? text, TuiToken token)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var code = Code(token);
        return code.Length == 0 ? text : code + text + Reset;
    }

    public static string BoldPaint(string? text) =>
        string.IsNullOrEmpty(text) || Level == TuiColorLevel.None ? text ?? string.Empty : Bold + text + Reset;

    public static string DimPaint(string? text) =>
        string.IsNullOrEmpty(text) || Level == TuiColorLevel.None ? text ?? string.Empty : Dim + text + Reset;

    // Role-named shortcuts for the CLI's own output (slash commands, notices, prompts). They keep
    // call sites readable and stop ad-hoc colour codes from drifting out of the palette.

    public static string Ok(string? text) => Paint(text, TuiToken.Ok);

    public static string Err(string? text) => Paint(text, TuiToken.Err);

    public static string Warn(string? text) => Paint(text, TuiToken.Warn);

    public static string Accent(string? text) => Paint(text, TuiToken.Brand);

    public static string Quiet(string? text) => Paint(text, TuiToken.Meta);

    /// <summary>Elevated foreground for centered panels (approval, pickers).</summary>
    public static string Panel(string? text) => Paint(text, TuiToken.Bright);

    /// <summary>Border and separator glyphs for panels, tables and cards.</summary>
    public static string Border(string? text) => Paint(text, TuiToken.Line);

    /// <summary>
    /// Background escape sequence for the user prompt block (panel surface).
    /// Returns empty string when color is disabled (None or Basic16).
    /// </summary>
    public static string SurfaceBgCode() => Level switch
    {
        TuiColorLevel.TrueColor => "\u001b[48;2;20;20;20m",
        TuiColorLevel.Palette256 => "\u001b[48;5;233m",
        _ => string.Empty,
    };

    /// <summary>
    /// Background escape sequence for the composer box interior (gray element surface).
    /// Returns empty string when color is disabled (None or Basic16).
    /// </summary>
    public static string ElementBgCode() => Level switch
    {
        TuiColorLevel.TrueColor => "\u001b[48;2;51;51;51m",
        TuiColorLevel.Palette256 => "\u001b[48;5;236m",
        _ => string.Empty,
    };

    /// <summary>Truecolor components shared by native attributes and text serialization.</summary>
    public static (int R, int G, int B) Rgb(TuiToken token) => token switch
    {
        TuiToken.Brand => (92, 156, 245),
        TuiToken.Brand2 => (157, 124, 216),
        TuiToken.Aurora => (0, 230, 118),
        TuiToken.AuroraDim => (0, 175, 95),
        TuiToken.Meta => (128, 128, 128),
        TuiToken.Line => (72, 72, 72),
        TuiToken.Ok => (127, 216, 143),
        TuiToken.Warn => (245, 167, 66),
        TuiToken.Err => (224, 108, 117),
        TuiToken.Bright => (238, 238, 238),
        _ => (238, 238, 238),
    };

    /// <summary>Composer interior gray band.</summary>
    public static (int R, int G, int B) ElementRgb() => (51, 51, 51);

    /// <summary>User-block panel surface.</summary>
    public static (int R, int G, int B) SurfaceRgb() => (20, 20, 20);

    private static string TrueColorParts(TuiToken token)
    {
        var (r, g, b) = Rgb(token);
        return $"{r};{g};{b}";
    }

    private static int Palette256Index(TuiToken token) => token switch
    {
        TuiToken.Brand => 75,
        TuiToken.Brand2 => 140,
        TuiToken.Aurora => 48,
        TuiToken.AuroraDim => 35,
        TuiToken.Meta => 244,
        TuiToken.Line => 238,
        TuiToken.Ok => 79,
        TuiToken.Warn => 215,
        TuiToken.Err => 174,
        TuiToken.Bright => 255,
        _ => 255,
    };

    private static int Basic16Index(TuiToken token) => token switch
    {
        TuiToken.Brand => 12,
        TuiToken.Brand2 => 13,
        TuiToken.Aurora => 10,
        TuiToken.AuroraDim => 2,
        TuiToken.Meta => 7,
        TuiToken.Line => 8,
        TuiToken.Ok => 10,
        TuiToken.Warn => 11,
        TuiToken.Err => 9,
        TuiToken.Bright => 15,
        _ => 7,
    };
}

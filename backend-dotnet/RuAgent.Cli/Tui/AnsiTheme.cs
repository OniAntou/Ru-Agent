namespace RuAgent.Cli.Tui;

public static class AnsiTheme
{
    private static bool? _enabledOverride;

    public static bool IsEnabled
    {
        get
        {
            if (_enabledOverride.HasValue) return _enabledOverride.Value;
            if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
            if (Console.IsOutputRedirected) return false;
            return true;
        }
        set => _enabledOverride = value;
    }

    public const string Reset = "\u001b[0m";
    public const string BoldCode = "\u001b[1m";
    public const string DimCode = "\u001b[2m";
    public const string ItalicCode = "\u001b[3m";
    public const string UnderlineCode = "\u001b[4m";

    public const string BlackCode = "\u001b[30m";
    public const string RedCode = "\u001b[31m";
    public const string GreenCode = "\u001b[32m";
    public const string YellowCode = "\u001b[33m";
    public const string BlueCode = "\u001b[34m";
    public const string MagentaCode = "\u001b[35m";
    public const string CyanCode = "\u001b[36m";
    public const string WhiteCode = "\u001b[37m";
    public const string GrayCode = "\u001b[90m";

    public const string BgBlueCode = "\u001b[44m";
    public const string BgDarkGrayCode = "\u001b[100m";

    public static string Color(string text, string ansiCode) =>
        IsEnabled ? $"{ansiCode}{text}{Reset}" : text;

    public static string Bold(string text) => Color(text, BoldCode);
    public static string Dim(string text) => Color(text, DimCode);
    public static string Green(string text) => Color(text, GreenCode);
    public static string Red(string text) => Color(text, RedCode);
    public static string Yellow(string text) => Color(text, YellowCode);
    public static string Blue(string text) => Color(text, BlueCode);
    public static string Magenta(string text) => Color(text, MagentaCode);
    public static string Cyan(string text) => Color(text, CyanCode);
    public static string Gray(string text) => Color(text, GrayCode);

    public static string Badge(string text, string code) =>
        IsEnabled ? $"{BoldCode}{code}[{text}]{Reset}" : $"[{text}]";

    public static string FormatDiffLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            return Bold(line);
        if (line.StartsWith("+", StringComparison.Ordinal))
            return Green(line);
        if (line.StartsWith("-", StringComparison.Ordinal))
            return Red(line);
        if (line.StartsWith("@@", StringComparison.Ordinal))
            return Cyan(line);
        return line;
    }
}

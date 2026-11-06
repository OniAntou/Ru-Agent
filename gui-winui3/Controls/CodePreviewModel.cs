using System.Globalization;
using System.Text;

namespace RuApp.Controls;

/// <summary>
/// What the file preview's code frame shows: the line-number gutter, the lines as
/// highlighted spans, and a one-line description ("Python · 343 lines"). Pure, so the
/// caps and the labels are testable without a window.
/// </summary>
public sealed record CodePreviewModel(string LineNumbers, IReadOnlyList<IReadOnlyList<CodeHighlighter.Span>> Lines, string Description)
{
    /// <summary>Above this size the text is shown plain: tinting a huge file costs more than it helps.</summary>
    public const int MaxHighlightedChars = 400_000;

    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csharp"] = "C#", ["python"] = "Python", ["powershell"] = "PowerShell", ["json"] = "JSON",
            ["xml"] = "XML", ["sql"] = "SQL", ["bash"] = "Shell", ["yaml"] = "YAML", ["diff"] = "Diff",
            ["javascript"] = "JavaScript", ["c"] = "C/C++", ["java"] = "Java", ["go"] = "Go", ["rust"] = "Rust",
            ["toml"] = "TOML", ["css"] = "CSS",
        };

    public static CodePreviewModel Build(string text, string? path)
    {
        var language = CodeHighlighter.LanguageForFile(path);
        bool highlight = language is not null && text.Length <= MaxHighlightedChars;
        var lines = CodeHighlighter.HighlightLines(text, highlight ? language : null);
        // A final newline ends the last line; it does not start an empty one.
        if (lines.Count > 1 && lines[^1].Count == 0) lines = lines.Take(lines.Count - 1).ToArray();

        var numbers = new StringBuilder();
        for (int index = 1; index <= lines.Count; index++)
        {
            if (index > 1) numbers.Append('\n');
            numbers.Append(index.ToString(CultureInfo.InvariantCulture));
        }

        string kind = language is not null && LanguageNames.TryGetValue(language, out var name) ? name : "Text";
        string count = lines.Count == 1 ? "1 line" : $"{lines.Count.ToString("N0", CultureInfo.InvariantCulture)} lines";
        string note = language is not null && !highlight ? " · colors off for large files" : string.Empty;
        return new CodePreviewModel(numbers.ToString(), lines, $"{kind} · {count}{note}");
    }
}

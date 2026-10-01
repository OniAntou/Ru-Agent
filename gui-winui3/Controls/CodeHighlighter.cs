namespace RuApp.Controls;

/// <summary>
/// Tiny deterministic code tinting for fenced code blocks: splits source into
/// plain/keyword/string/comment/number spans per language. A highlighter
/// library would do better, but chat code is short and this stays dependency
/// free with bounded output.
/// </summary>
public static class CodeHighlighter
{
    public enum SpanKind
    {
        Plain,
        Keyword,
        String,
        Comment,
        Number,
        Added,
        Removed,
        HunkHeader,
    }

    public sealed record Span(string Text, SpanKind Kind);

    private static readonly IReadOnlyDictionary<string, Language> Languages =
        new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                Keywords("abstract as async await base bool break byte case catch char class const continue decimal default do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in int interface internal is lock long namespace new null object operator out override params partial private protected public readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while"),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\'']),
            ["python"] = new(
                Keywords("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield"),
                LineComments: ["#"],
                BlockComment: null,
                Strings: ['"', '\'']),
            ["powershell"] = new(
                Keywords("function param begin process end if else elseif switch foreach for while do until break continue return try catch finally throw trap using class enum"),
                LineComments: ["#"],
                BlockComment: ("<#", "#>"),
                Strings: ['"', '\'']),
            ["json"] = new(
                Keywords("true false null"),
                LineComments: [],
                BlockComment: null,
                Strings: ['"']),
            ["xml"] = new XmlLanguage(),
            ["sql"] = new(
                Keywords("select from where join on left right inner outer full group by order having insert into values update set delete create table alter drop index view as and or not null primary key foreign references distinct top limit offset union all"),
                LineComments: ["--"],
                BlockComment: ("/*", "*/"),
                Strings: ['\''],
                CaseInsensitiveKeywords: true),
            ["bash"] = new(
                Keywords("if then else elif fi for while do done case esac function in select until echo cd ls exit export local readonly declare"),
                LineComments: ["#"],
                BlockComment: null,
                Strings: ['"', '\'']),
            ["yaml"] = new(
                Keywords("true false null"),
                LineComments: ["#"],
                BlockComment: null,
                Strings: ['"', '\'']),
            ["diff"] = new DiffLanguage(),
        };

    private static HashSet<string> Keywords(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static IReadOnlyList<Span> Highlight(string code, string? language)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Array.Empty<Span>();
        }

        var normalized = NormalizeLanguage(language);
        if (normalized is not null && Languages.TryGetValue(normalized, out var definition))
        {
            return definition.Tokenize(code);
        }

        return new[] { new Span(code, SpanKind.Plain) };
    }

    internal static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var id = language.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return id switch
        {
            "cs" or "c#" or "dotnet" => "csharp",
            "ps1" or "psm1" => "powershell",
            "sh" or "shell" or "zsh" => "bash",
            "yml" => "yaml",
            "xaml" or "axaml" => "xml",
            "py" => "python",
            _ => id,
        };
    }

    private record Language(
        HashSet<string> Keywords,
        IReadOnlyList<string> LineComments,
        (string Open, string Close)? BlockComment,
        char[] Strings,
        bool CaseInsensitiveKeywords = false)
    {
        public IReadOnlyList<Span> Tokenize(string code) =>
            CodeHighlighter.Tokenize(code, this);
    }

    private sealed record XmlLanguage : Language
    {
        public XmlLanguage()
            : base(new HashSet<string>(StringComparer.Ordinal), Array.Empty<string>(), null, Array.Empty<char>())
        {
        }
    }

    private sealed record DiffLanguage : Language
    {
        public DiffLanguage()
            : base(new HashSet<string>(StringComparer.Ordinal), Array.Empty<string>(), null, Array.Empty<char>())
        {
        }
    }

    private static IReadOnlyList<Span> Tokenize(string code, Language language)
    {
        if (language is XmlLanguage)
        {
            return TokenizeXml(code);
        }

        if (language is DiffLanguage)
        {
            return TokenizeDiff(code);
        }

        var spans = new List<Span>();
        var current = new System.Text.StringBuilder();
        void FlushPlain()
        {
            if (current.Length > 0)
            {
                FlushWords(spans, current.ToString(), language);
                current.Clear();
            }
        }

        int index = 0;
        while (index < code.Length)
        {
            if (language.BlockComment is (string blockOpen, string blockClose)
                && code.AsSpan(index).StartsWith(blockOpen, StringComparison.Ordinal))
            {
                FlushPlain();
                int end = code.IndexOf(blockClose, index + blockOpen.Length, StringComparison.Ordinal);
                int stop = end < 0 ? code.Length : end + blockClose.Length;
                spans.Add(new Span(code.Substring(index, stop - index), SpanKind.Comment));
                index = stop;
                continue;
            }

            string? lineComment = null;
            foreach (var marker in language.LineComments)
            {
                if (code.AsSpan(index).StartsWith(marker, StringComparison.Ordinal))
                {
                    lineComment = marker;
                    break;
                }
            }

            if (lineComment is not null)
            {
                FlushPlain();
                int end = code.IndexOf('\n', index);
                int stop = end < 0 ? code.Length : end;
                spans.Add(new Span(code.Substring(index, stop - index), SpanKind.Comment));
                index = stop;
                continue;
            }

            char quote = code[index];
            if (Array.IndexOf(language.Strings, quote) >= 0)
            {
                FlushPlain();
                int stop = index + 1;
                while (stop < code.Length)
                {
                    if (code[stop] == '\\' && stop + 1 < code.Length)
                    {
                        stop += 2;
                        continue;
                    }

                    stop++;
                    if (code[stop - 1] == quote)
                    {
                        break;
                    }
                }

                spans.Add(new Span(code.Substring(index, stop - index), SpanKind.String));
                index = stop;
                continue;
            }

            if (char.IsDigit(code[index]) && (index == 0 || !char.IsLetterOrDigit(code[index - 1]) && code[index - 1] != '_'))
            {
                FlushPlain();
                int stop = index;
                while (stop < code.Length && (char.IsDigit(code[stop]) || code[stop] == '.' || code[stop] == '_'))
                {
                    stop++;
                }

                spans.Add(new Span(code.Substring(index, stop - index), SpanKind.Number));
                index = stop;
                continue;
            }

            current.Append(code[index]);
            index++;
        }

        FlushPlain();
        return spans;
    }

    private static void FlushWords(List<Span> spans, string text, Language language)
    {
        int index = 0;
        while (index < text.Length)
        {
            if (char.IsLetter(text[index]) || text[index] == '_' || text[index] == '$' || text[index] == '@')
            {
                int stop = index + 1;
                while (stop < text.Length && (char.IsLetterOrDigit(text[stop]) || text[stop] == '_' || text[stop] == '-'))
                {
                    stop++;
                }

                var word = text.Substring(index, stop - index);
                var lookup = language.CaseInsensitiveKeywords ? word.ToLowerInvariant() : word;
                spans.Add(new Span(word, language.Keywords.Contains(lookup) ? SpanKind.Keyword : SpanKind.Plain));
                index = stop;
            }
            else
            {
                int stop = index + 1;
                while (stop < text.Length && !(char.IsLetter(text[stop]) || text[stop] == '_' || text[stop] == '$' || text[stop] == '@'))
                {
                    stop++;
                }

                spans.Add(new Span(text.Substring(index, stop - index), SpanKind.Plain));
                index = stop;
            }
        }
    }

    private static IReadOnlyList<Span> TokenizeXml(string code)
    {
        var spans = new List<Span>();
        int index = 0;
        while (index < code.Length)
        {
            int open = code.IndexOf('<', index);
            if (open < 0)
            {
                spans.Add(new Span(code.Substring(index), SpanKind.Plain));
                break;
            }

            if (open > index)
            {
                spans.Add(new Span(code.Substring(index, open - index), SpanKind.Plain));
            }

            if (code.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = code.IndexOf("-->", open, StringComparison.Ordinal);
                int stop = end < 0 ? code.Length : end + 3;
                spans.Add(new Span(code.Substring(open, stop - open), SpanKind.Comment));
                index = stop;
                continue;
            }

            int close = code.IndexOf('>', open);
            int tagStop = close < 0 ? code.Length : close + 1;
            AddXmlTag(spans, code.Substring(open, tagStop - open));
            index = tagStop;
        }

        return spans;
    }

    private static void AddXmlTag(List<Span> spans, string tag)
    {
        int index = 0;
        while (index < tag.Length)
        {
            int quote = tag.IndexOf('"', index);
            if (quote < 0)
            {
                spans.Add(new Span(tag.Substring(index), SpanKind.Keyword));
                return;
            }

            if (quote > index)
            {
                spans.Add(new Span(tag.Substring(index, quote - index), SpanKind.Keyword));
            }

            int stop = tag.IndexOf('"', quote + 1);
            int end = stop < 0 ? tag.Length : stop + 1;
            spans.Add(new Span(tag.Substring(quote, end - quote), SpanKind.String));
            index = end;
        }
    }

    private static IReadOnlyList<Span> TokenizeDiff(string code)
    {
        var spans = new List<Span>();
        var parts = code.Split('\n');
        for (int index = 0; index < parts.Length; index++)
        {
            bool last = index == parts.Length - 1;
            if (last && parts[index].Length == 0)
            {
                break;
            }

            var line = parts[index];
            SpanKind kind = line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)
                ? SpanKind.Added
                : line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal)
                    ? SpanKind.Removed
                    : line.StartsWith("@", StringComparison.Ordinal)
                        ? SpanKind.HunkHeader
                        : SpanKind.Plain;
            spans.Add(new Span(last ? line : line + "\n", kind));
        }

        return spans;
    }
}

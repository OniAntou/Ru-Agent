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
        /// <summary>An identifier directly followed by "(": a call or a definition.</summary>
        Function,
        /// <summary>A capitalized identifier in a case-sensitive language: usually a type.</summary>
        Type,
    }

    private const string CFamilyKeywords =
        "auto break case char const continue default do double else enum extern float for goto if inline int long register return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false nullptr class namespace template typename public private protected virtual override new delete this using try catch throw operator friend constexpr noexcept include define ifdef ifndef endif pragma";

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
                Strings: ['"', '\''],
                DashInIdentifiers: true),
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
                Strings: ['"', '\''],
                DashInIdentifiers: true),
            ["yaml"] = new(
                Keywords("true false null"),
                LineComments: ["#"],
                BlockComment: null,
                Strings: ['"', '\''],
                DashInIdentifiers: true),
            ["diff"] = new DiffLanguage(),
            ["javascript"] = new(
                Keywords("async await break case catch class const continue debugger default delete do else export extends false finally for from function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield as interface type enum implements private protected public readonly declare namespace abstract keyof never unknown any number string boolean"),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\'', '`']),
            ["c"] = new(
                Keywords(CFamilyKeywords),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\'']),
            ["java"] = new(
                Keywords("abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long native new null package private protected public return short static super switch synchronized this throw throws transient true false try var void volatile while record fun val object when override data sealed open internal companion"),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\'']),
            ["go"] = new(
                Keywords("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil string int int64 float64 bool byte error"),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\'', '`']),
            ["rust"] = new(
                Keywords("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while i32 i64 u8 u32 u64 usize f64 bool str String Vec Option Some None Ok Err Result"),
                LineComments: ["//"],
                BlockComment: ("/*", "*/"),
                Strings: ['"']),
            ["toml"] = new(
                Keywords("true false"),
                LineComments: ["#"],
                BlockComment: null,
                Strings: ['"', '\'']),
            ["css"] = new(
                Keywords("important media import from to"),
                LineComments: [],
                BlockComment: ("/*", "*/"),
                Strings: ['"', '\''],
                CaseInsensitiveKeywords: true,
                DashInIdentifiers: true),
        };

    private static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp", [".csx"] = "csharp",
            [".py"] = "python", [".pyw"] = "python", [".pyi"] = "python",
            [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
            [".json"] = "json", [".jsonc"] = "json",
            [".xml"] = "xml", [".xaml"] = "xml", [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml",
            [".html"] = "xml", [".htm"] = "xml", [".svg"] = "xml", [".resx"] = "xml", [".config"] = "xml",
            [".sql"] = "sql",
            [".sh"] = "bash", [".bash"] = "bash", [".zsh"] = "bash",
            [".yml"] = "yaml", [".yaml"] = "yaml",
            [".diff"] = "diff", [".patch"] = "diff",
            [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".ts"] = "javascript", [".tsx"] = "javascript",
            [".c"] = "c", [".h"] = "c", [".cpp"] = "c", [".cc"] = "c", [".cxx"] = "c", [".hpp"] = "c",
            [".java"] = "java", [".kt"] = "java", [".kts"] = "java",
            [".go"] = "go",
            [".rs"] = "rust",
            [".toml"] = "toml", [".ini"] = "toml", [".cfg"] = "toml",
            [".css"] = "css", [".scss"] = "css", [".less"] = "css",
        };

    /// <summary>The highlighter language for a file name by its extension, or null when none fits.</summary>
    public static string? LanguageForFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var extension = System.IO.Path.GetExtension(fileName.Trim());
        return extension.Length > 0 && Extensions.TryGetValue(extension, out var language) ? language : null;
    }

    /// <summary>
    /// Highlights the whole text, so comments and strings that span lines keep their
    /// kind, then splits the spans into lines. Line breaks are not part of any span.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Span>> HighlightLines(string? code, string? language)
    {
        var lines = new List<IReadOnlyList<Span>>();
        var current = new List<Span>();
        foreach (var span in Highlight(code?.Replace("\r\n", "\n").Replace('\r', '\n'), language))
        {
            var parts = span.Text.Split('\n');
            for (int index = 0; index < parts.Length; index++)
            {
                if (index > 0)
                {
                    lines.Add(current);
                    current = new List<Span>();
                }

                if (parts[index].Length > 0) current.Add(new Span(parts[index], span.Kind));
            }
        }

        lines.Add(current);
        return lines;
    }

    private static HashSet<string> Keywords(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static IReadOnlyList<Span> Highlight(string? code, string? language)
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
            "xaml" or "axaml" or "html" or "htm" or "svg" => "xml",
            "py" => "python",
            "js" or "jsx" or "ts" or "tsx" or "typescript" or "mjs" => "javascript",
            "cpp" or "c++" or "cc" or "h" or "hpp" => "c",
            "kotlin" or "kt" => "java",
            "golang" => "go",
            "rs" => "rust",
            "ini" => "toml",
            "scss" or "less" => "css",
            _ => id,
        };
    }

    private record Language(
        HashSet<string> Keywords,
        IReadOnlyList<string> LineComments,
        (string Open, string Close)? BlockComment,
        char[] Strings,
        bool CaseInsensitiveKeywords = false,
        bool DashInIdentifiers = false)
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
                // Cmdlets (Get-Item) and CSS names (font-size) keep their dashes; in other
                // languages a dash is an operator, so x-y stays three tokens.
                while (stop < text.Length && (char.IsLetterOrDigit(text[stop]) || text[stop] == '_'
                    || (language.DashInIdentifiers && text[stop] == '-')))
                {
                    stop++;
                }

                var word = text.Substring(index, stop - index);
                var lookup = language.CaseInsensitiveKeywords ? word.ToLowerInvariant() : word;
                var kind = language.Keywords.Contains(lookup) ? SpanKind.Keyword
                    : stop < text.Length && text[stop] == '(' ? SpanKind.Function
                    : !language.CaseInsensitiveKeywords && char.IsUpper(word[0]) && word.Any(char.IsLower) ? SpanKind.Type
                    : SpanKind.Plain;
                spans.Add(new Span(word, kind));
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

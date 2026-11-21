namespace RuAgent.Client.Attachments;

/// <summary>
/// The text and code files both clients inline (or reference) as attachments. One list,
/// so a file the desktop app accepts is not refused by the terminal, which had a shorter
/// copy without <c>.jsonc</c>, <c>.vue</c>, <c>Dockerfile</c> and the rest.
/// </summary>
public static class AttachmentFileTypes
{
    public static bool IsTextOrCodeFile(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Rakefile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Gemfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("README", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".dockerignore", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".nvmrc", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".env", StringComparison.OrdinalIgnoreCase);
        }

        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".json" or ".jsonc" or ".xml" or ".svg" or ".yaml" or ".yml"
            or ".csv" or ".tsv" or ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".htm"
            or ".css" or ".scss" or ".sass" or ".less" or ".sql" or ".sh" or ".bash" or ".zsh" or ".bat"
            or ".cmd" or ".ps1" or ".psm1" or ".psd1" or ".rs" or ".go" or ".cpp" or ".cc" or ".c" or ".h"
            or ".hpp" or ".java" or ".kt" or ".kts" or ".swift" or ".dart" or ".php" or ".rb" or ".lua"
            or ".ini" or ".cfg" or ".conf" or ".log" or ".env" or ".toml" or ".proto" or ".graphql"
            or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".razor" or ".cshtml" or ".vue"
            or ".svelte" or ".gradle" or ".groovy" or ".m" or ".mm" or ".scala" or ".sc" or ".sol" or ".asm"
            or ".s" or ".fs" or ".fsx" or ".vb" or ".r" or ".pl" or ".ex" or ".exs" or ".erl" or ".hrl"
            or ".clj" or ".cljs" or ".hs" or ".lhs" or ".ml" or ".mli" or ".tex" or ".rst" or ".adoc"
            or ".properties" or ".editorconfig" or ".gitignore" or ".gitattributes" or ".dockerignore"
            => true,
            _ => false
        };
    }
}

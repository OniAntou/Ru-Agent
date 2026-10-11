using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Files;

internal sealed class FileGlobPattern
{
    private readonly Regex _regex;
    private readonly bool _matchFileName;

    public FileGlobPattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        _matchFileName = !normalized.Contains('/');
        var expression = new StringBuilder("^");
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
            {
                index++;
                if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                {
                    index++;
                    expression.Append("(?:.*/)?"); // **/ includes zero directories.
                }
                else expression.Append(".*");
            }
            else if (character == '*') expression.Append("[^/]*");
            else if (character == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(character.ToString()));
        }
        expression.Append('$');
        _regex = new Regex(expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            | RegexOptions.NonBacktracking | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
    }

    public bool IsMatch(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return _regex.IsMatch(normalized) || (_matchFileName && _regex.IsMatch(Path.GetFileName(normalized)));
    }
}

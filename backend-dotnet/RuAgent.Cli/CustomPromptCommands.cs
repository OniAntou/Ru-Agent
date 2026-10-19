using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Cli;

/// <summary>A prompt template found for a custom slash command.</summary>
public sealed record CustomPromptCommand(string Name, string Description, string Template);

/// <summary>
/// Loads user-defined slash commands from Markdown templates:
/// <c>&lt;workspace&gt;/.ru/commands/&lt;name&gt;.md</c> first, then
/// <c>~/.ru/commands/&lt;name&gt;.md</c>. Expansion yields an ordinary user prompt, so a
/// template grants no tool, path or approval authority. Built-in commands win because
/// callers consult this only for names they do not handle.
/// </summary>
public static partial class CustomPromptCommands
{
    public const int MaxTemplateBytes = 64 * 1024;
    public const string ArgumentsPlaceholder = "$ARGUMENTS";
    private const string CommandDirectory = "commands";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Finds the template for a slash token such as <c>/review</c>; null when absent or unusable.</summary>
    public static CustomPromptCommand? Find(string slashToken, string workspace, string? userHome)
    {
        if (slashToken is not ['/', .. var name] || !NamePattern().IsMatch(name)) return null;
        foreach (var root in Roots(workspace, userHome))
        {
            var file = Path.Combine(root, ".ru", CommandDirectory, name + ".md");
            var text = TryReadTemplate(file);
            if (text is not null) return Parse(name, text);
        }

        return null;
    }

    /// <summary>Names of all usable templates, workspace entries shadowing user ones.</summary>
    public static IReadOnlyList<string> List(string workspace, string? userHome)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in Roots(workspace, userHome))
        {
            var directory = Path.Combine(root, ".ru", CommandDirectory);
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (NamePattern().IsMatch(name) && TryReadTemplate(file) is not null) names.Add(name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        return [.. names];
    }

    /// <summary>Substitutes <c>$ARGUMENTS</c>; without a placeholder, arguments are appended after the template.</summary>
    public static string Expand(CustomPromptCommand command, string? arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        var value = arguments?.Trim() ?? string.Empty;
        if (command.Template.Contains(ArgumentsPlaceholder, StringComparison.Ordinal))
            return command.Template.Replace(ArgumentsPlaceholder, value, StringComparison.Ordinal).Trim();
        return value.Length == 0 ? command.Template.Trim() : command.Template.TrimEnd() + "\n\n" + value;
    }

    internal static CustomPromptCommand Parse(string name, string text)
    {
        text = text.TrimStart('﻿').Replace("\r\n", "\n");
        var description = string.Empty;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[4..end].Split('\n'))
                {
                    if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        description = line["description:".Length..].Trim().Trim('"', '\'');
                }

                var bodyStart = text.IndexOf('\n', end + 1);
                text = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..];
            }
        }

        return new CustomPromptCommand(name, description, text);
    }

    private static IEnumerable<string> Roots(string workspace, string? userHome)
    {
        if (!string.IsNullOrWhiteSpace(workspace)) yield return workspace;
        if (!string.IsNullOrWhiteSpace(userHome)) yield return userHome;
    }

    private static string? TryReadTemplate(string file)
    {
        try
        {
            var info = new FileInfo(file);
            // A link could point outside the command directory; templates must be plain files.
            if (!info.Exists || info.Length > MaxTemplateBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(file));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }
}

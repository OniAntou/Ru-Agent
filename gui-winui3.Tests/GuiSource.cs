using System.Xml.Linq;

namespace RuApp.Tests;

/// <summary>
/// Locates the shipped GUI sources and reads XAML as XML.
///
/// The point of this helper is to stop asserting on source text. A test that greps
/// for <c>x:Name="NavChat"</c> fails when an attribute is reordered or a comment
/// changes, yet passes when the element stops being reachable. Reading the markup
/// as markup keeps the test honest: it breaks on a real change to the tree and
/// ignores formatting.
///
/// One kind of text assertion is still legitimate, and the helpers here exist to make
/// it precise: that a decision lives in one place rather than being re-derived in the
/// view. <see cref="MethodBody"/> scopes it to the member that makes the decision, and
/// <see cref="StringLiterals"/> pins the absence of a literal that should have come
/// from a shared module. Both are about the seam, not about wording. Asserting that a
/// name merely appears somewhere in a file is not — that only breaks on a rename.
/// </summary>
internal static class GuiSource
{
    public static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Absolute path of a file under the gui-winui3 project root.</summary>
    public static string Path(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string root = System.IO.Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(root))
            {
                return System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }

    public static bool Exists(params string[] parts) => File.Exists(Path(parts));

    public static string Text(params string[] parts) => File.ReadAllText(Path(parts));

    /// <summary>
    /// Concatenated C# source of every partial-class file of a view
    /// (<c>ChatView.xaml.cs</c> plus its <c>ChatView.*.cs</c> partials,
    /// <c>MainWindow.xaml.cs</c> plus its partials). A source contract pins
    /// where a decision lives in the *class*; which file of the partial class
    /// holds it is an implementation detail that must not change the test.
    /// </summary>
    public static string ViewText(string viewName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string root = System.IO.Path.Combine(directory.FullName, "gui-winui3");
            if (!Directory.Exists(root))
            {
                continue;
            }

            var sources = Directory.EnumerateFiles(root, viewName + "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                    && !file.Contains(
                        $"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
            if (sources.Count == 0)
            {
                throw new FileNotFoundException($"No source file for view '{viewName}'.");
            }

            return string.Join("\n", sources.Select(File.ReadAllText));
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }

    public static XDocument Xml(params string[] parts) => XDocument.Load(Path(parts));

    /// <summary>Every element carrying an <c>x:Name</c>, keyed by that name.</summary>
    public static Dictionary<string, XElement> NamedElements(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var named = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in document.Descendants())
        {
            if (element.Attribute(Xaml + "Name")?.Value is { Length: > 0 } name)
            {
                named[name] = element;
            }
        }

        return named;
    }

    /// <summary>
    /// Values of a markup attribute across every element that declares it, in
    /// document order. Used for <c>Click</c>, <c>Tag</c>, <c>AutomationProperties.*</c>
    /// and similar handler/style bindings.
    /// </summary>
    public static List<string> AttributeValues(XDocument document, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var values = new List<string>();
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, attributeName, StringComparison.Ordinal)
                    || string.Equals(attribute.Name.LocalName, attributeName.Split('.').Last(), StringComparison.Ordinal))
                {
                    values.Add(attribute.Value);
                }
            }
        }

        return values;
    }

    /// <summary>Text content of every attribute named <paramref name="attributeLocalName"/>.</summary>
    public static List<string> AttributeLocalValues(XDocument document, string attributeLocalName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var values = new List<string>();
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, attributeLocalName, StringComparison.Ordinal))
                {
                    values.Add(attribute.Value);
                }
            }
        }

        return values;
    }

    /// <summary>Every literal string in XAML that is bound to a handler with <c>Click="..."</c>.</summary>
    public static List<string> ClickHandlers(XDocument document) =>
        AttributeLocalValues(document, "Click");

    public static List<string> XamlNames(XDocument document) =>
        NamedElements(document).Keys.ToList();

    /// <summary>
    /// Reads a markup attribute by its local name. Attached properties arrive as
    /// <c>Grid.RowSpan</c> (a dot keeps them unprefixed), so the segment after the
    /// last dot is what gets compared. The <c>x:</c> namespace is skipped: its
    /// <c>x:Name</c> has the local name <c>Name</c> and would otherwise answer a
    /// lookup for <c>AutomationProperties.Name</c>.
    /// </summary>
    public static string? AttributeValue(XElement element, string localName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(localName);
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name;
            if (string.Equals(name.NamespaceName, Xaml.NamespaceName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name.LocalName, localName, StringComparison.Ordinal)
                || string.Equals(name.ToString(), localName, StringComparison.Ordinal)
                || name.LocalName.EndsWith("." + localName, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    public static bool HasAttribute(XElement element, string localName) =>
        AttributeValue(element, localName) is not null;

    public static int IntAttribute(XElement element, string localName, int fallback = 0) =>
        int.TryParse(
            AttributeValue(element, localName),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    /// <summary>Whether <paramref name="ancestor"/> contains <paramref name="element"/>.</summary>
    public static bool IsDescendantOf(XElement element, XElement ancestor)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(ancestor);
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Document order index of an element within its document, for ordering assertions.</summary>
    public static int DocumentOrder(XDocument document, XElement element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);
        var index = 0;
        foreach (var candidate in document.Descendants())
        {
            if (ReferenceEquals(candidate, element))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Finds a named element, failing the assertion rather than returning null.</summary>
    public static XElement Require(XDocument document, string xamlName)
    {
        var named = NamedElements(document);
        if (!named.TryGetValue(xamlName, out var element))
        {
            throw new Xunit.Sdk.XunitException($"x:Name=\"{xamlName}\" was not found in the markup.");
        }

        return element;
    }

    /// <summary>
    /// The body of a C# member, from its signature line to its matching close brace.
    ///
    /// Reading a method keeps an assertion about what it does honest, and unlike
    /// searching the whole file it cannot be satisfied by an unrelated line elsewhere.
    /// </summary>
    public static string MethodBody(string code, string memberName)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        // The name must be the first identifier on a line: matching it anywhere would
        // also match a call such as `_store.Clear(` inside another body, and the brace
        // scan would then run from the wrong place. The prefix therefore allows type
        // words but not a qualified receiver, and the parameter list is consumed so the
        // scan resumes where the body must begin.
        var declaration = DeclarationPattern(memberName);

        // A call on its own line reads exactly like a declaration, so the first match is
        // often `ClearComposerDraft(sessionId);` rather than the method. Only a match
        // followed by a body can be the declaration.
        foreach (System.Text.RegularExpressions.Match candidate in declaration.Matches(code))
        {
            int afterParams = code.IndexOf(')', candidate.Index + candidate.Length - 1);
            if (afterParams < 0)
            {
                continue;
            }

            int start = afterParams + 1;
            while (start < code.Length && char.IsWhiteSpace(code[start]))
            {
                start++;
            }

            if (TryReadMemberBody(code, start, memberName, out var body))
            {
                return body;
            }
        }

        throw new Xunit.Sdk.XunitException($"Member \"{memberName}\" was not found with a readable body.");
    }

    /// <summary>
    /// Reads the body starting at <paramref name="start"/>, which must be an opening
    /// brace or the arrow of an expression body.
    /// </summary>
    private static bool TryReadMemberBody(string code, int start, string memberName, out string body)
    {
        body = string.Empty;
        if (start >= code.Length)
        {
            return false;
        }

        // An expression-bodied member has no block, but it is still the member's body:
        // reading it is how a delegating one-liner gets asserted.
        if (code[start] == '=' && start + 1 < code.Length && code[start + 1] == '>')
        {
            int expressionEnd = code.IndexOf(';', start);
            if (expressionEnd < 0)
            {
                throw new Xunit.Sdk.XunitException($"Member \"{memberName}\" has an unterminated expression body.");
            }

            body = code[start..(expressionEnd + 1)];
            return true;
        }

        if (code[start] != '{')
        {
            return false;
        }

        var depth = 0;
        for (var index = start; index < code.Length; index++)
        {
            if (code[index] == '{')
            {
                depth++;
            }
            else if (code[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    body = code[start..(index + 1)];
                    return true;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Member \"{memberName}\" has an unbalanced body.");
    }

    /// <summary>
    /// Matches a member declaration line.
    ///
    /// The prefix is kept to the pieces a declaration actually uses — modifiers, then
    /// one return type — and joined to the name without allowing a space, because
    /// allowing one lets <c>_store. Clear(</c> read as a declaration of <c>Clear</c>.
    /// A dotted receiver never appears in the prefix, so a call cannot match.
    /// </summary>
    private static System.Text.RegularExpressions.Regex DeclarationPattern(string memberName) =>
        new(@"(?m)^[ \t]*(?:(?:public|private|protected|internal|static|async|override|sealed|virtual|partial|readonly|unsafe|extern|new)[ \t]+)*"
            + @"(?:[A-Za-z_][\w<>\[\],\?]*[ \t]+)?"
            + System.Text.RegularExpressions.Regex.Escape(memberName)
            + @"[ \t]*\(");

    /// <summary>
    /// Whether the code declares a member with this name.
    ///
    /// Used to close the loop between markup and code: a handler the XAML wires must
    /// exist, or the control silently does nothing when clicked.
    /// </summary>
    public static bool HasMember(string code, string memberName)
    {
        ArgumentNullException.ThrowIfNull(code);
        return !string.IsNullOrWhiteSpace(memberName) && DeclarationPattern(memberName).IsMatch(code);
    }

    /// <summary>Every string literal in a snippet, in source order.</summary>
    public static IReadOnlyList<string> StringLiterals(string snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);

        var literals = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(snippet, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            literals.Add(match.Groups[1].Value);
        }

        return literals;
    }
}

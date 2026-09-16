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
}

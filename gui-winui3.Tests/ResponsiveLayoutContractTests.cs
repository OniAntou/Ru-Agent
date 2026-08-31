using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace RuApp.Tests;

public sealed class ResponsiveLayoutContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex ResourceReferencePattern = new(
        @"\{(?:StaticResource|ThemeResource)\s+(?<key>(?:Ru|App)\w+)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string AutomationIdAttribute = "AutomationProperties.AutomationId";
    private const string AutomationNameAttribute = "AutomationProperties.Name";
    // Stable commands with explicit names; not a snapshot of translated labels.
    private static readonly string[] NamedCommandIds =
    [
        "NewSessionButton", "MicButton", "AgentModeButton", "AgentStopButton",
        "AgentTrustDenyButton", "AgentTrustApproveButton", "AgentDenyButton",
        "AgentApproveButton", "AgentContinueButton",
    ];

    [Fact]
    public void Existing_xaml_has_no_unresolved_Ru_or_App_resource_references()
    {
        var documents = LoadXamlDocuments();
        var appDocument = documents.Single(document => Path.GetFileName(document.Path) == "App.xaml");
        var appKeys = GetResourceKeys(appDocument.Document);
        var unresolved = new List<string>();

        foreach (var (path, document) in documents)
        {
            var visibleKeys = new HashSet<string>(appKeys, StringComparer.Ordinal);
            if (!string.Equals(path, appDocument.Path, StringComparison.Ordinal))
            {
                visibleKeys.UnionWith(GetResourceKeys(document));
            }

            foreach (var attribute in document.Descendants().Attributes())
            {
                foreach (Match match in ResourceReferencePattern.Matches(attribute.Value))
                {
                    var key = match.Groups["key"].Value;
                    if (!visibleKeys.Contains(key))
                    {
                        unresolved.Add($"{Path.GetFileName(path)}: {key}");
                    }
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"Unresolved Ru/App resource references:{Environment.NewLine}{string.Join(Environment.NewLine, unresolved)}");
    }

    [Fact]
    public void Primary_command_controls_keep_explicit_automation_names()
    {
        var documents = LoadXamlDocuments();
        var commandControls = documents
            .SelectMany(document => document.Document.Descendants())
            .Where(element => IsCommandControl(element) && FindAttachedAttribute(element, AutomationIdAttribute) is not null)
            .ToArray();

        Assert.NotEmpty(commandControls);

        foreach (var id in NamedCommandIds)
        {
            var control = Assert.Single(commandControls,
                element => FindAttachedAttribute(element, AutomationIdAttribute)!.Value == id);
            Assert.False(HasMissingAttachedName(control), $"Missing accessible name: {id}");
        }
    }

    [Fact]
    public void Accessibility_check_rejects_a_realistic_missing_attached_name_mutation()
    {
        var document = LoadXamlDocuments()
            .Single(document => Path.GetFileName(document.Path) == "MainWindow.xaml")
            .Document;
        var button = document.Descendants()
            .Single(element =>
                IsCommandControl(element)
                && FindAttachedAttribute(element, AutomationIdAttribute)?.Value == "NewSessionButton");

        Assert.NotNull(button.Attribute(XamlNamespace + "Name"));
        Assert.False(HasMissingAttachedName(button));
        FindAttachedAttribute(button, AutomationNameAttribute)!.Remove();

        Assert.True(HasMissingAttachedName(button));
    }

    private static bool IsCommandControl(XElement element)
    {
        return element.Name.LocalName is "Button" or "ToggleButton";
    }

    private static bool HasMissingAttachedName(XElement element)
    {
        var automationId = FindAttachedAttribute(element, AutomationIdAttribute);
        var automationName = FindAttachedAttribute(element, AutomationNameAttribute);
        return IsCommandControl(element)
            && automationId is not null
            && (automationName is null || string.IsNullOrWhiteSpace(automationName.Value));
    }

    private static XAttribute? FindAttachedAttribute(XElement element, string localName)
    {
        return element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName);
    }

    private static IReadOnlySet<string> GetResourceKeys(XDocument document)
    {
        return document
            .Descendants()
            .Select(element => element.Attribute(XamlNamespace + "Key"))
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<(string Path, XDocument Document)> LoadXamlDocuments()
    {
        var sourceRoot = FindSourceRoot();
        var paths = Directory
            .EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => path
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .All(segment =>
                    !string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(paths);

        var documents = paths
            .Select(path => (path, XDocument.Load(path)))
            .ToArray();

        Assert.NotEmpty(documents);
        return documents;
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return sourceRoot;
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}

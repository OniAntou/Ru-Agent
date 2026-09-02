using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Help;

public sealed record BundledHelpDocumentSource(string DocumentId, string ResourceName);

public sealed record BundledHelpSection(
    string DocumentId,
    string Title,
    string Section,
    string Text,
    int DocumentOrdinal,
    int SectionOrdinal);

public sealed class BundledHelpDocsCatalog
{
    public const int MaxDocumentBytes = 256 * 1024;
    public const int MaxCombinedBytes = 1024 * 1024;

    public static IReadOnlyList<BundledHelpDocumentSource> DefaultSources { get; } =
    [
        new("ru-overview", "RuAgent.Broker.HelpDocs.ru-overview.md"),
        new("architecture", "RuAgent.Broker.HelpDocs.architecture.md"),
        new("runtime", "RuAgent.Broker.HelpDocs.runtime.md"),
        new("agent-tools", "RuAgent.Broker.HelpDocs.agent-tools.md"),
        new("provider-catalog", "RuAgent.Broker.HelpDocs.provider-catalog.md"),
    ];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex Heading = new("^(?<hash>#{1,3})\\s+(?<text>\\S(?:.*\\S)?)\\s*#*\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<BundledHelpSection> _sections;

    private BundledHelpDocsCatalog(IReadOnlyList<BundledHelpSection> sections)
    {
        _sections = sections;
    }

    public IReadOnlyList<BundledHelpSection> Sections => _sections;

    public static BundledHelpDocsCatalog LoadDefault(Assembly? assembly = null)
    {
        assembly ??= typeof(BundledHelpDocsCatalog).Assembly;
        var resources = new List<(BundledHelpDocumentSource Source, byte[] Bytes)>();
        foreach (var source in DefaultSources)
        {
            using var stream = assembly.GetManifestResourceStream(source.ResourceName)
                ?? throw InvalidCatalog($"missing_resource:{source.ResourceName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            resources.Add((source, bytes));
        }

        return FromBytes(resources);
    }

    public static BundledHelpDocsCatalog FromText(IReadOnlyList<(BundledHelpDocumentSource Source, string Text)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return FromBytes(documents.Select(item => (item.Source, StrictUtf8.GetBytes(item.Text))).ToArray());
    }

    private static BundledHelpDocsCatalog FromBytes(IReadOnlyList<(BundledHelpDocumentSource Source, byte[] Bytes)> documents)
    {
        if (documents.Count != DefaultSources.Count
            || documents.Select(item => item.Source).Zip(DefaultSources).Any(pair => !Equals(pair.First, pair.Second))
            || documents.Select(item => item.Source.DocumentId).Distinct(StringComparer.Ordinal).Count() != documents.Count
            || documents.Select(item => item.Source.ResourceName).Distinct(StringComparer.Ordinal).Count() != documents.Count)
        {
            throw InvalidCatalog("duplicate_or_missing_resource");
        }

        var totalBytes = 0;
        var sections = new List<BundledHelpSection>();
        for (var documentOrdinal = 0; documentOrdinal < documents.Count; documentOrdinal++)
        {
            var (source, bytes) = documents[documentOrdinal];
            if (bytes.Length > MaxDocumentBytes || (totalBytes += bytes.Length) > MaxCombinedBytes)
            {
                throw InvalidCatalog("resource_too_large");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            }
            catch (DecoderFallbackException error)
            {
                throw InvalidCatalog("invalid_utf8", error);
            }

            sections.AddRange(ParseSections(source.DocumentId, text, documentOrdinal));
        }

        if (sections.Count == 0)
        {
            throw InvalidCatalog("no_searchable_sections");
        }

        return new BundledHelpDocsCatalog(Array.AsReadOnly(sections.ToArray()));
    }

    private static IEnumerable<BundledHelpSection> ParseSections(string documentId, string text, int documentOrdinal)
    {
        var lines = text.Split('\n');
        string? title = null;
        var currentHeading = "Overview";
        var currentContent = new List<string>();
        var sectionOrdinal = 0;
        var sawH2 = false;
        var sections = new List<BundledHelpSection>();

        void Flush()
        {
            if (title is null || currentContent.All(string.IsNullOrWhiteSpace))
            {
                currentContent.Clear();
                return;
            }

            var body = string.Join('\n', currentContent).Trim();
            if (body.Length > 0)
            {
                sections.Add(new BundledHelpSection(documentId, title, currentHeading, body, documentOrdinal, sectionOrdinal++));
            }

            currentContent.Clear();
        }

        var headingPath = new string?[3];
        foreach (var line in lines)
        {
            var match = Heading.Match(line);
            if (!match.Success)
            {
                currentContent.Add(line);
                continue;
            }

            var level = match.Groups["hash"].Value.Length;
            var heading = match.Groups["text"].Value.Trim();
            if (level == 1)
            {
                title ??= heading;
                continue;
            }

            if (title is null)
            {
                continue;
            }

            if (level == 2)
            {
                Flush();
                sawH2 = true;
                headingPath[0] = heading;
                headingPath[1] = null;
                headingPath[2] = null;
                currentHeading = heading;
            }
            else if (level == 3)
            {
                Flush();
                headingPath[1] = heading;
                headingPath[2] = null;
                currentHeading = sawH2 && headingPath[0] is not null
                    ? $"{headingPath[0]} > {heading}"
                    : heading;
            }
        }

        Flush();
        if (title is null)
        {
            throw InvalidCatalog($"missing_h1:{documentId}");
        }

        if (sections.Count == 0)
        {
            throw InvalidCatalog($"no_searchable_sections:{documentId}");
        }

        return sections;
    }

    private static InvalidOperationException InvalidCatalog(string detail, Exception? inner = null) =>
        new($"help_docs_catalog_invalid:{detail}", inner);
}

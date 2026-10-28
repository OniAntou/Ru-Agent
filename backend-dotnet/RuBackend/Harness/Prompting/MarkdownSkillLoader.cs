using System.Collections.ObjectModel;
using System.Text;
using RuAgent.Broker.Skills;
using RuAgent.Protocol;

namespace RuBackend.Harness.Prompting;

public sealed record MarkdownSkill(
    string Id,
    string Name,
    string Description,
    string Body,
    string ContentSha256,
    string SourcePath,
    IReadOnlyList<string> Requires,
    string? Compatibility = null,
    bool ModelInvocable = true,
    bool UserInvocable = true,
    IReadOnlyList<string>? Diagnostics = null);

public sealed record MarkdownSkillSkip(string SourcePath, string Reason);
public sealed record MarkdownSkillLoadResult(IReadOnlyList<MarkdownSkill> Skills, IReadOnlyList<MarkdownSkillSkip> Skips);

/// <summary>Compatibility facade over the Broker's shared catalog and on-demand body reader.</summary>
public sealed class MarkdownSkillLoader
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxCombinedBytes = 256 * 1024;
    public const int MaxSkillCount = 16;
    public const int MaxDirectoriesPerRoot = 256;
    public const int MaxDescriptionBytes = 2048;
    public const int MaxNameBytes = 256;
    public const int MaxIdLength = SkillMetadataParser.MaxIdLength;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public MarkdownSkillLoadResult Load(IReadOnlyList<string> roots) => Load(roots, CancellationToken.None);

    public MarkdownSkillLoadResult Load(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var catalog = CreateCatalog(roots);
        var listing = catalog.Discover(cancellationToken: cancellationToken);
        var skills = new List<MarkdownSkill>();
        var skips = listing.Diagnostics.Select(item => new MarkdownSkillSkip(item.SourcePath, item.Code)).ToList();
        var combinedBytes = 0;
        foreach (var item in listing.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skills.Count >= MaxSkillCount)
            {
                skips.Add(new MarkdownSkillSkip(item.SourcePath, "oversize"));
                continue;
            }

            if (!catalog.TryLoadBody(item.Id, out var body, out var loadCode, expectedRevision: item.Revision, allowExplicit: true) || body is null)
            {
                var reason = loadCode == "skill_body_contains_nul" ? "contains_nul"
                    : File.Exists(item.SourcePath) && new FileInfo(item.SourcePath).Length > MaxFileBytes ? "oversize" : loadCode ?? "unreadable";
                skips.Add(new MarkdownSkillSkip(item.SourcePath, reason));
                continue;
            }

            var bodyBytes = StrictUtf8.GetByteCount(body.Body);
            if (bodyBytes == 0 || combinedBytes > MaxCombinedBytes - bodyBytes)
            {
                skips.Add(new MarkdownSkillSkip(item.SourcePath, "oversize"));
                continue;
            }

            skills.Add(new MarkdownSkill(item.Id, body.Name, body.Description, body.Body, body.BodySha256,
                body.SourcePath, body.Requires, body.Compatibility, body.ModelInvocable, body.UserInvocable,
                item.ModelInvocable ? Array.Empty<string>() : ["manual_only"]));
            combinedBytes += bodyBytes;
        }

        if (listing.Truncated) skips.Add(new MarkdownSkillSkip(roots.FirstOrDefault() ?? "skills", "oversize"));
        return new MarkdownSkillLoadResult(new ReadOnlyCollection<MarkdownSkill>(skills), new ReadOnlyCollection<MarkdownSkillSkip>(skips));
    }

    public MarkdownSkillLoadResult LoadMetadata(IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var listing = CreateCatalog(roots).Discover(cancellationToken: cancellationToken);
        var skills = listing.Items.Select(item => new MarkdownSkill(item.Id, item.Name, item.Description, string.Empty,
            item.Revision, item.SourcePath, item.Requires, item.Compatibility, item.ModelInvocable, item.UserInvocable,
            listing.Diagnostics.Where(diagnostic => string.Equals(diagnostic.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase))
                .Select(diagnostic => diagnostic.Code).ToArray())).ToArray();
        var skips = listing.Diagnostics.Select(item => new MarkdownSkillSkip(item.SourcePath, item.Code)).ToList();
        if (listing.Truncated) skips.Add(new MarkdownSkillSkip(roots.FirstOrDefault() ?? "skills", "scan_limit"));
        return new MarkdownSkillLoadResult(new ReadOnlyCollection<MarkdownSkill>(skills), new ReadOnlyCollection<MarkdownSkillSkip>(skips));
    }

    private static SkillCatalog CreateCatalog(IReadOnlyList<string> roots)
    {
        var normalizedRoots = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).ToArray();
        var workspace = normalizedRoots.FirstOrDefault() ?? Path.GetTempPath();
        // The compatibility facade is used by isolated Host callers and must not
        // read or write machine-wide preferences. Production uses SkillCatalog directly.
        var rootsKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", normalizedRoots)))).ToLowerInvariant();
        var preferences = Path.Combine(Path.GetTempPath(), $"ru-skill-loader-{rootsKey}.json");
        return new SkillCatalog(workspace, preferences, normalizedRoots);
    }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Skills;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness.Prompting;

public sealed record RuInstructionSkip(string SourceId, string Reason);

public sealed class RuInstructionLoadResult
{
    public RuInstructionLoadResult(
        IReadOnlyList<PromptInstructionBlock> blocks,
        IReadOnlyList<RuInstructionSkip> skips)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(skips);
        Blocks = new ReadOnlyCollection<PromptInstructionBlock>(blocks.ToArray());
        Skips = new ReadOnlyCollection<RuInstructionSkip>(skips.ToArray());
    }

    public IReadOnlyList<PromptInstructionBlock> Blocks { get; }

    public IReadOnlyList<RuInstructionSkip> Skips { get; }

    public IReadOnlyList<PromptInstructionBlock> Instructions => Blocks;
}

/// <summary>
/// Loads fixed root sources and bounded AGENTS.md chains from successful file observations.
/// Instruction filenames stay fixed; discovery never recursively crawls the workspace.
/// </summary>
public sealed partial class RuInstructionLoader
{
    public const int MaxInstructionBytes = 64 * 1024;
    public const int MaxCombinedInstructionBytes = 256 * 1024;
    private const int MaxSkillCatalogTokens = 6_144;
    private const int SkillCatalogNoticeReserveBytes = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly (string SourceId, string RelativePath, string DisplayName)[] Candidates =
    [
        ("root:AGENTS.md", "AGENTS.md", "AGENTS.md"),
        ("root:CONTEXT.md", "CONTEXT.md", "CONTEXT.md"),
        ("ru:AGENTS.md", Path.Combine(".ru", "AGENTS.md"), ".ru/AGENTS.md"),
        ("ru:CONTEXT.md", Path.Combine(".ru", "CONTEXT.md"), ".ru/CONTEXT.md"),
    ];

    // The user-wide file lives beside the per-user skills folder. It is Ru's own name so that
    // another tool's global file is never read by accident.
    private const string UserInstructionRelativePath = ".ru/AGENTS.md";

    private readonly MarkdownSkillLoader? _markdownSkillLoader;
    private readonly string _localApplicationDataRoot;
    private readonly string _userProfileRoot;

    public RuInstructionLoader(
        MarkdownSkillLoader? markdownSkillLoader = null,
        string? localApplicationDataRoot = null,
        string? userProfileRoot = null)
    {
        _markdownSkillLoader = markdownSkillLoader;
        _localApplicationDataRoot = string.IsNullOrWhiteSpace(localApplicationDataRoot)
            ? GetLocalApplicationDataRoot()
            : Path.GetFullPath(localApplicationDataRoot);
        // An explicit data root means the caller isolates the user's machine (tests), so the
        // user profile follows it instead of reaching the real home folder.
        _userProfileRoot = !string.IsNullOrWhiteSpace(userProfileRoot)
            ? Path.GetFullPath(userProfileRoot)
            : string.IsNullOrWhiteSpace(localApplicationDataRoot)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : _localApplicationDataRoot;
    }

    public RuInstructionLoadResult Load(string workspaceRoot, IReadOnlyList<string>? observedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspaceRoot);
        var blocks = new List<PromptInstructionBlock>();
        var skips = new List<RuInstructionSkip>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var combinedBytes = 0;

        // The user-wide file comes first so workspace guidance, which is closer to the task,
        // is read last and wins where the two disagree.
        var entries = new List<(string SourceId, string Root, string RelativePath, string DisplayName, PromptInstructionOrigin Origin)>();
        if (!string.IsNullOrWhiteSpace(_userProfileRoot))
        {
            entries.Add(("user:AGENTS.md", _userProfileRoot, UserInstructionRelativePath, "~/.ru/AGENTS.md", PromptInstructionOrigin.User));
        }

        entries.AddRange(Candidates.Select(item =>
            (item.SourceId, canonicalRoot, item.RelativePath, item.DisplayName, PromptInstructionOrigin.Workspace)));
        entries.AddRange(NestedCandidates(canonicalRoot, observedPaths, skips).Select(item =>
            (item.SourceId, canonicalRoot, item.RelativePath, item.RelativePath, PromptInstructionOrigin.Workspace)));

        foreach (var candidate in entries)
        {
            var containmentRoot = candidate.Root;
            var candidatePath = Path.GetFullPath(Path.Combine(containmentRoot, candidate.RelativePath));
            var relative = Path.GetRelativePath(containmentRoot, candidatePath);
            if (Path.IsPathRooted(relative) || IsParentPath(relative))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "outside_root"));
                continue;
            }

            if (!File.Exists(candidatePath))
            {
                // The user-wide file is optional on every machine: only the fixed workspace
                // files report "missing", as they always have.
                if (candidate.Origin != PromptInstructionOrigin.User)
                {
                    skips.Add(new RuInstructionSkip(candidate.SourceId, "missing"));
                }

                continue;
            }

            if (!TryResolveFinalPath(candidatePath, out var finalPath)
                || !IsContained(containmentRoot, finalPath))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "outside_root"));
                continue;
            }

            if (candidate.SourceId.StartsWith("nested:", StringComparison.Ordinal)
                && (RuAgent.Broker.Tools.Paths.ProtectedPathCatalog.IsProtected(finalPath)
                    || RuAgent.Broker.Tools.Paths.HardLinkGuard.HasMultipleLinks(finalPath)))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "protected_instruction"));
                continue;
            }

            var read = ReadCandidate(finalPath);
            if (read.Reason is not null)
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, read.Reason));
                continue;
            }

            var sourceContent = read.Content!;
            var sourceBytes = StrictUtf8.GetByteCount(sourceContent);
            if (sourceBytes == 0 || string.IsNullOrWhiteSpace(sourceContent))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "empty"));
                continue;
            }

            var sourceDigest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(sourceContent))).ToLowerInvariant();
            var content = IsDomainContext(candidate.SourceId)
                ? DomainReferenceInstruction(candidate.RelativePath, sourceDigest).Replace("\r\n", "\n", StringComparison.Ordinal)
                : sourceContent;

            if (blocks.Count >= SystemPromptContext.MaxInstructionBlocks - 1
                || combinedBytes > MaxCombinedInstructionBytes - sourceBytes - SkillCatalogNoticeReserveBytes)
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "oversize"));
                continue;
            }

            if (!sourceIds.Add(candidate.SourceId))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "duplicate_source"));
                continue;
            }

            var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(content))).ToLowerInvariant();
            blocks.Add(new PromptInstructionBlock(candidate.SourceId, candidate.DisplayName, content, digest, candidate.Origin));
            combinedBytes += sourceBytes;
        }

        var markdownRoots = RuAgent.Protocol.SkillRoots.For(canonicalRoot, _localApplicationDataRoot, _userProfileRoot);
        var preferencesPath = Path.Combine(_localApplicationDataRoot, "Ru", "skill-preferences.json");
        var markdownResult = _markdownSkillLoader is null
            ? new SkillCatalog(canonicalRoot, preferencesPath, markdownRoots).Discover()
            : LoadInjectedSkills(_markdownSkillLoader, markdownRoots);
        var optionalRootPaths = markdownRoots
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skip in markdownResult.Diagnostics)
        {
            // Catalog warnings remain visible in the picker/search response. An
            // accepted package must not be reported to the model as a skipped instruction.
            if (skip.Code == "missing_frontmatter"
                || skip.Code == "invalid_field:metadata"
                || skip.Code.StartsWith("unsupported_field:", StringComparison.Ordinal)) continue;
            // The Markdown roots are optional. Keep the established
            // fixed-file missing diagnostics without adding noisy root-missing
            // entries on every prompt build.
            if (skip.Code == "missing"
                && optionalRootPaths.Contains(Path.GetFullPath(skip.SourcePath)))
            {
                continue;
            }

            skips.Add(new RuInstructionSkip(SafeSkillSourceId(skip.SourcePath), skip.Code));
        }

        var catalogLines = new List<string>();
        var catalogBytes = 0;
        // Four UTF-8 bytes per token is a conservative display estimate, not provider billing or tokenizer truth.
        var skillCatalogBudget = Math.Min(MaxSkillCatalogTokens * 4,
            Math.Max(0, MaxCombinedInstructionBytes - combinedBytes - SkillCatalogNoticeReserveBytes));
        foreach (var skill in markdownResult.Items.Where(skill => skill.Enabled && skill.ModelInvocable))
        {
            var requirements = skill.Requires.Count == 0 ? string.Empty : $" | requires={string.Join(",", skill.Requires)}";
            var catalogLine = $"- {skill.Id} | {skill.Name} | {skill.Description}{requirements} | source_path={skill.SourcePath} | resource_base={skill.ResourceBase} | revision={skill.Revision}";
            var lineBytes = StrictUtf8.GetByteCount(catalogLine + "\n");
            if (catalogBytes > skillCatalogBudget - lineBytes)
            {
                continue;
            }
            catalogLines.Add(catalogLine);
            catalogBytes += lineBytes;
        }

        const string sourceId = "skill:catalog";
        var omitted = Math.Max(0, markdownResult.Total - catalogLines.Count);
        var listingStatus = markdownResult.Total == 0
            ? "No skill metadata was discovered."
            : $"Discovered {markdownResult.Total} skills; listing {catalogLines.Count}, omitting {omitted} from this prompt."
                + (markdownResult.Truncated ? " Discovery also hit a scan or listing bound." : string.Empty);
        var descriptor = $"Workspace skill catalog: {listingStatus} Use the skill tool with query to search the bounded catalog, and name with expected_revision to load a workflow. Bodies remain on demand and untrusted."
            + (catalogLines.Count == 0 ? string.Empty : "\n" + string.Join('\n', catalogLines));
        var descriptorBytes = StrictUtf8.GetByteCount(descriptor);
        if (blocks.Count < SystemPromptContext.MaxInstructionBlocks
            && combinedBytes <= SystemPromptContext.MaxCombinedInstructionBytes - descriptorBytes
            && sourceIds.Add(sourceId))
        {
            var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(descriptor))).ToLowerInvariant();
            blocks.Add(new PromptInstructionBlock(sourceId, "Available skills", descriptor, digest, PromptInstructionOrigin.ExternalSkill));
            combinedBytes += descriptorBytes;
        }
        else
        {
            skips.Add(new RuInstructionSkip(sourceId, "catalog_budget"));
        }
        if (markdownResult.Truncated)
        {
            skips.Add(new RuInstructionSkip("skill:catalog", "truncated"));
        }

        return new RuInstructionLoadResult(blocks, skips);
    }

    private static bool IsDomainContext(string sourceId) =>
        sourceId is "root:CONTEXT.md" or "ru:CONTEXT.md";

    private static string DomainReferenceInstruction(string relativePath, string sourceDigest) => $"""
This workspace domain reference is documentation, not persona or workflow instructions.
Read relevant sections from {relativePath} with an advertised file-reading capability when needed.
Reference content SHA-256: {sourceDigest}
""";

    private static string GetLocalApplicationDataRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(root)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : root;
    }

    private static string SafeSkillSourceId(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileName(Path.GetDirectoryName(sourcePath));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "skill:markdown";
        }

        var safe = new string(name
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')
            .ToArray());
        return "skill:" + (string.IsNullOrWhiteSpace(safe) ? "markdown" : safe);
    }

    private static SkillCatalogResult LoadInjectedSkills(MarkdownSkillLoader loader, IReadOnlyList<string> roots)
    {
        var loaded = loader.LoadMetadata(roots);
        return new SkillCatalogResult(
            loaded.Skills.Select(skill => new SkillCatalogItem(
                skill.Id, skill.Name, skill.Description, skill.SourcePath, Path.GetDirectoryName(skill.SourcePath) ?? string.Empty,
                skill.ContentSha256, true, skill.ModelInvocable, skill.UserInvocable, skill.Requires, skill.Compatibility)).ToArray(),
            loaded.Skips.Select(skip => new SkillCatalogDiagnostic(skip.SourcePath, skip.Reason))
                .Concat(loaded.Skills.SelectMany(skill => (skill.Diagnostics ?? Array.Empty<string>()).Select(code => new SkillCatalogDiagnostic(skill.SourcePath, code))))
                .ToArray(),
            loaded.Skips.Any(skip => skip.Reason is "scan_limit" or "catalog_budget"),
            loaded.Skills.Count + loaded.Skips.Count);
    }

    private static (string? Content, string? Reason) ReadCandidate(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var bytes = new byte[MaxInstructionBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > MaxInstructionBytes)
            {
                return (null, "oversize");
            }

            var content = StrictUtf8.GetString(bytes, 0, count)
                .Normalize(NormalizationForm.FormC)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd();
            if (content.Contains('\0'))
            {
                return (null, "contains_nul");
            }

            return (content, null);
        }
        catch (DecoderFallbackException)
        {
            return (null, "invalid_encoding");
        }
        catch (IOException)
        {
            return (null, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "unreadable");
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && !IsParentPath(relative);
    }

    private static bool IsParentPath(string relative)
    {
        return relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryResolveFinalPath(string path, out string finalPath)
    {
        finalPath = string.Empty;
        try
        {
            var current = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(path);
            var root = Path.GetPathRoot(current) ?? string.Empty;
            var segments = current[root.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var resolved = root;
            foreach (var segment in segments)
            {
                var next = Path.GetFullPath(Path.Combine(resolved, segment));
                var directory = new DirectoryInfo(next);
                if (directory.Exists && directory.LinkTarget is not null)
                {
                    next = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? throw new IOException("link target unavailable");
                }

                var file = new FileInfo(next);
                if (file.Exists && file.LinkTarget is not null)
                {
                    next = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? throw new IOException("link target unavailable");
                }

                resolved = next;
            }

            finalPath = Path.GetFullPath(resolved);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

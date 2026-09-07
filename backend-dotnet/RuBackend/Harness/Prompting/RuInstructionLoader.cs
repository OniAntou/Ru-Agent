using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
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
/// Loads only the fixed Ru instruction files below a trusted run root.
/// No model-provided path or recursive discovery is accepted here.
/// </summary>
public sealed class RuInstructionLoader
{
    public const int MaxInstructionBytes = 64 * 1024;
    public const int MaxCombinedInstructionBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly (string SourceId, string RelativePath, string DisplayName)[] Candidates =
    [
        ("root:Ru.md", "Ru.md", "Ru.md"),
        ("ru:AGENTS.md", Path.Combine(".ru", "AGENTS.md"), ".ru/AGENTS.md"),
        ("ru:CONTEXT.md", Path.Combine(".ru", "CONTEXT.md"), ".ru/CONTEXT.md"),
    ];

    private readonly MarkdownSkillLoader _markdownSkillLoader;
    private readonly string _localApplicationDataRoot;

    public RuInstructionLoader(
        MarkdownSkillLoader? markdownSkillLoader = null,
        string? localApplicationDataRoot = null)
    {
        _markdownSkillLoader = markdownSkillLoader ?? new MarkdownSkillLoader();
        _localApplicationDataRoot = string.IsNullOrWhiteSpace(localApplicationDataRoot)
            ? GetLocalApplicationDataRoot()
            : Path.GetFullPath(localApplicationDataRoot);
    }

    public RuInstructionLoadResult Load(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspaceRoot);
        var blocks = new List<PromptInstructionBlock>();
        var skips = new List<RuInstructionSkip>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var combinedBytes = 0;

        foreach (var candidate in Candidates)
        {
            var candidatePath = Path.GetFullPath(Path.Combine(canonicalRoot, candidate.RelativePath));
            var relative = Path.GetRelativePath(canonicalRoot, candidatePath);
            if (Path.IsPathRooted(relative) || IsParentPath(relative))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "outside_root"));
                continue;
            }

            if (!File.Exists(candidatePath))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "missing"));
                continue;
            }

            if (!TryResolveFinalPath(candidatePath, out var finalPath)
                || !IsContained(canonicalRoot, finalPath))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "outside_root"));
                continue;
            }

            var read = ReadCandidate(finalPath);
            if (read.Reason is not null)
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, read.Reason));
                continue;
            }

            var content = read.Content!;
            var contentBytes = StrictUtf8.GetByteCount(content);
            if (contentBytes == 0 || string.IsNullOrWhiteSpace(content))
            {
                skips.Add(new RuInstructionSkip(candidate.SourceId, "empty"));
                continue;
            }

            if (combinedBytes > MaxCombinedInstructionBytes - contentBytes)
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
            blocks.Add(new PromptInstructionBlock(candidate.SourceId, candidate.DisplayName, content, digest));
            combinedBytes += contentBytes;
        }

        var markdownRoots = new[]
        {
            Path.Combine(canonicalRoot, "skills"),
            Path.Combine(canonicalRoot, ".ru", "skills"),
            Path.Combine(_localApplicationDataRoot, "Ru", "skills"),
        };
        var markdownResult = _markdownSkillLoader.Load(markdownRoots);
        var optionalRootPaths = markdownRoots
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skip in markdownResult.Skips)
        {
            // The three Markdown roots are optional. Keep the established
            // fixed-file missing diagnostics without adding noisy root-missing
            // entries on every prompt build.
            if (skip.Reason == "missing"
                && optionalRootPaths.Contains(Path.GetFullPath(skip.SourcePath)))
            {
                continue;
            }

            skips.Add(new RuInstructionSkip(SafeSkillSourceId(skip.SourcePath), skip.Reason));
        }

        foreach (var skill in markdownResult.Skills)
        {
            var sourceId = $"skill:{skill.Id}";
            var contentBytes = StrictUtf8.GetByteCount(skill.Body);
            if (blocks.Count >= SystemPromptContext.MaxInstructionBlocks
                || combinedBytes > SystemPromptContext.MaxCombinedInstructionBytes - contentBytes)
            {
                skips.Add(new RuInstructionSkip(sourceId, "oversize"));
                continue;
            }

            if (!sourceIds.Add(sourceId))
            {
                skips.Add(new RuInstructionSkip(sourceId, "duplicate_source"));
                continue;
            }

            var displayName = SkillDisplayName(skill.Name, skill.Description);
            blocks.Add(new PromptInstructionBlock(
                sourceId,
                displayName,
                skill.Body,
                skill.ContentSha256,
                PromptInstructionOrigin.ExternalSkill));
            combinedBytes += contentBytes;
        }

        return new RuInstructionLoadResult(blocks, skips);
    }

    private static string GetLocalApplicationDataRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(root)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : root;
    }

    private static string SkillDisplayName(string name, string description)
    {
        const int maxCharacters = 256;
        var prefix = string.IsNullOrWhiteSpace(name) ? "Markdown skill" : name.Trim();
        var suffix = string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description.Trim()}";
        if (prefix.Length + suffix.Length <= maxCharacters)
        {
            return prefix + suffix;
        }

        var available = Math.Max(0, maxCharacters - prefix.Length);
        return available == 0 ? prefix[..maxCharacters] : prefix + suffix[..available];
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

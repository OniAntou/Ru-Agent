using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness.Prompting;

public sealed record MarkdownSkill(
    string Id,
    string Name,
    string Description,
    string Body,
    string ContentSha256,
    string SourcePath);

public sealed record MarkdownSkillSkip(string SourcePath, string Reason);

public sealed record MarkdownSkillLoadResult(
    IReadOnlyList<MarkdownSkill> Skills,
    IReadOnlyList<MarkdownSkillSkip> Skips);

/// <summary>
/// Discovers bounded, untrusted SKILL.md guidance from caller-ordered roots.
/// This loader does not expose metadata as capabilities or execute package files.
/// </summary>
public sealed class MarkdownSkillLoader
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxCombinedBytes = 256 * 1024;
    public const int MaxSkillCount = 16;
    public const int MaxDirectoriesPerRoot = 256;
    public const int MaxDescriptionBytes = 2048;
    public const int MaxNameBytes = 256;
    public const int MaxIdLength = 96;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public MarkdownSkillLoadResult Load(IReadOnlyList<string> roots) =>
        Load(roots, CancellationToken.None);

    public MarkdownSkillLoadResult Load(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        cancellationToken.ThrowIfCancellationRequested();

        var skills = new List<MarkdownSkill>();
        var skips = new List<MarkdownSkillSkip>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var combinedBytes = 0;

        foreach (var configuredRoot in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.GetFullPath(configuredRoot);
            }
            catch (ArgumentException)
            {
                skips.Add(new MarkdownSkillSkip(configuredRoot, "unreadable"));
                continue;
            }

            if (!Directory.Exists(root))
            {
                skips.Add(new MarkdownSkillSkip(root, "missing"));
                continue;
            }

            if (IsReparsePoint(root))
            {
                skips.Add(new MarkdownSkillSkip(root, "reparse_point"));
                continue;
            }

            try
            {
                root = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(root);
            }
            catch (ArgumentException)
            {
                skips.Add(new MarkdownSkillSkip(root, "unreadable"));
                continue;
            }
            if (IsReparsePoint(root))
            {
                skips.Add(new MarkdownSkillSkip(root, "reparse_point"));
                continue;
            }

            string[] directories;
            var directoryEnumerationTruncated = false;
            try
            {
                var discoveredDirectories = new SortedSet<string>(
                    Comparer<string>.Create(CompareDirectoryOrdinal));
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    if (discoveredDirectories.Count >= MaxDirectoriesPerRoot)
                    {
                        directoryEnumerationTruncated = true;
                        var last = discoveredDirectories.Max!;
                        if (CompareDirectoryOrdinal(directory, last) < 0)
                        {
                            discoveredDirectories.Remove(last);
                            discoveredDirectories.Add(directory);
                        }

                        continue;
                    }

                    discoveredDirectories.Add(directory);
                }

                directories = discoveredDirectories.ToArray();
            }
            catch (IOException)
            {
                skips.Add(new MarkdownSkillSkip(root, "unreadable"));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skips.Add(new MarkdownSkillSkip(root, "unreadable"));
                continue;
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(directory))
                {
                    skips.Add(new MarkdownSkillSkip(directory, "reparse_point"));
                    continue;
                }

                var finalDirectory = ResolveFinalPath(directory);
                if (finalDirectory is null || !IsContained(root, finalDirectory))
                {
                    skips.Add(new MarkdownSkillSkip(directory, "outside_root"));
                    continue;
                }

                var skillFile = FindSkillFile(directory);
                if (skillFile is null)
                {
                    skips.Add(new MarkdownSkillSkip(directory, "missing"));
                    continue;
                }

                if (IsReparsePoint(skillFile))
                {
                    skips.Add(new MarkdownSkillSkip(skillFile, "reparse_point"));
                    continue;
                }

                var finalFile = ResolveFinalPath(skillFile);
                if (finalFile is null || !IsContained(root, finalFile))
                {
                    skips.Add(new MarkdownSkillSkip(skillFile, "outside_root"));
                    continue;
                }

                var read = ReadSkillFile(skillFile, root);
                if (read.Reason is not null)
                {
                    skips.Add(new MarkdownSkillSkip(finalFile, read.Reason));
                    continue;
                }

                var parsed = Parse(finalFile, read.Content!);
                if (parsed.Reason is not null)
                {
                    skips.Add(new MarkdownSkillSkip(finalFile, parsed.Reason));
                    continue;
                }

                var skill = parsed.Skill!;
                if (seenIds.Contains(skill.Id))
                {
                    skips.Add(new MarkdownSkillSkip(finalFile, "duplicate"));
                    continue;
                }

                var bodyBytes = StrictUtf8.GetByteCount(skill.Body);
                if (skills.Count >= MaxSkillCount || bodyBytes == 0 || combinedBytes > MaxCombinedBytes - bodyBytes)
                {
                    skips.Add(new MarkdownSkillSkip(finalFile, "oversize"));
                    continue;
                }

                seenIds.Add(skill.Id);
                skills.Add(skill);
                combinedBytes += bodyBytes;
            }

            if (directoryEnumerationTruncated)
            {
                skips.Add(new MarkdownSkillSkip(root, "oversize"));
            }
        }

        return new MarkdownSkillLoadResult(
            new ReadOnlyCollection<MarkdownSkill>(skills),
            new ReadOnlyCollection<MarkdownSkillSkip>(skips));
    }

    private static string? FindSkillFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string? Content, string? Reason) ReadSkillFile(string path, string root)
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

            var openedFinalPath = ResolveOpenedFinalPath(stream, path);
            if (openedFinalPath is null)
            {
                return (null, "unreadable");
            }

            var packageDirectory = Path.GetDirectoryName(path);
            if (IsReparsePoint(path)
                || packageDirectory is null
                || IsReparsePoint(packageDirectory))
            {
                return (null, "reparse_point");
            }

            if (!IsContained(root, openedFinalPath))
            {
                return (null, "outside_root");
            }

            var bytes = new byte[MaxFileBytes + 1];
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

            if (count > MaxFileBytes)
            {
                return (null, "oversize");
            }

            var content = StrictUtf8.GetString(bytes, 0, count)
                .TrimStart('\uFEFF')
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

    private static string? ResolveOpenedFinalPath(FileStream stream, string fallbackPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(fallbackPath);
        }

        var buffer = new StringBuilder(32 * 1024);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        var finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            finalPath = @"\\" + finalPath[8..];
        }
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            finalPath = finalPath[4..];
        }

        try
        {
            return Path.GetFullPath(finalPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (MarkdownSkill? Skill, string? Reason) Parse(string path, string content)
    {
        var parsedFrontMatter = ParseFrontMatter(content);
        var body = parsedFrontMatter.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, "empty");
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? "skill");
        var name = LimitUtf8(parsedFrontMatter.Name ?? folderName, MaxNameBytes);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "skill";
        }

        var description = LimitUtf8(parsedFrontMatter.Description ?? DeriveDescription(body), MaxDescriptionBytes);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "Markdown skill guidance";
        }

        var id = DeriveId(parsedFrontMatter.Name ?? folderName);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = DeriveId(folderName);
        }

        var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(body))).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id))
        {
            id = "skill-" + digest[..16];
        }

        return (new MarkdownSkill(id, name, description, body, digest, path), null);
    }

    private static (string? Name, string? Description, string Body) ParseFrontMatter(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length < 3 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            return (null, null, content);
        }

        var closing = -1;
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
            {
                closing = index;
                break;
            }
        }

        if (closing < 0)
        {
            return (null, null, content);
        }

        string? name = null;
        string? description = null;
        for (var index = 1; index < closing; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (value.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "name":
                    name ??= value;
                    break;
                case "description":
                    description ??= value;
                    break;
                case "allowed-tools":
                    // Intentionally parsed as descriptive metadata only. It never
                    // becomes a capability, tool grant, or Broker policy input.
                    _ = value;
                    break;
            }
        }

        var body = string.Join('\n', lines[(closing + 1)..]).TrimStart('\n').TrimEnd();
        return (name, description, body);
    }

    private static string DeriveDescription(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (candidate[0] == '#')
            {
                candidate = candidate.TrimStart('#').Trim();
            }

            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return "Markdown skill guidance";
    }

    private static string DeriveId(string value)
    {
        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in value.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if ((character is >= 'a' and <= 'z') || (character is >= '0' and <= '9') || character is '.' or '_')
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else if (character == '-')
            {
                pendingSeparator = true;
            }
            else
            {
                pendingSeparator = true;
            }

            if (builder.Length >= MaxIdLength)
            {
                break;
            }
        }

        return builder.ToString().Trim('-', '.', '_');
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static string LimitUtf8(string value, int maxBytes)
    {
        value = value.Normalize(NormalizationForm.FormC).Trim();
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        for (var length = maxBytes; length > 0; length--)
        {
            try
            {
                return StrictUtf8.GetString(bytes, 0, length).TrimEnd();
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return string.Empty;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? ResolveFinalPath(string path)
    {
        try
        {
            return WindowsWorkspaceIdentityResolver.GetCanonicalRoot(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static int CompareDirectoryOrdinal(string left, string right)
    {
        var byName = string.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right),
            StringComparison.Ordinal);
        return byName != 0
            ? byName
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        int cchFilePath,
        uint dwFlags);
}

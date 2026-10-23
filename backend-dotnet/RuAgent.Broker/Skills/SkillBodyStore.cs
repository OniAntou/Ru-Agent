using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public sealed record SkillBody(string Name, string Description, string Body, string BodySha256, string SourcePath, IReadOnlyList<string> Requires);

public sealed class SkillBodyStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _workspaceRoot;

    public SkillBodyStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public SkillBody? LoadBody(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var root in SkillRoots())
        {
            var body = TryLoadFromRoot(root, name.Trim());
            if (body is not null)
            {
                return body;
            }
        }

        return null;
    }

    private IEnumerable<string> SkillRoots() => RuAgent.Protocol.SkillRoots.For(_workspaceRoot);

    private static SkillBody? TryLoadFromRoot(string root, string name)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!Directory.Exists(fullRoot))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string skillFile;
            try
            {
                skillFile = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                    ?? string.Empty;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (string.IsNullOrEmpty(skillFile))
            {
                return null;
            }

            // A SKILL.md reached through a link could name a file outside the
            // skill root it was discovered under. Resolve the final path and read
            // only that, so a reparse point cannot escape its root.
            string canonicalFile;
            try
            {
                var resolved = new CanonicalPathResolver(fullRoot).Resolve(skillFile);
                if (!resolved.IsInsideWorkspace)
                {
                    return null;
                }

                canonicalFile = resolved.CanonicalPath;
            }
            catch (ArgumentException)
            {
                return null;
            }

            var read = ReadSkillFile(canonicalFile);
            if (read is null)
            {
                return null;
            }

            var parsed = ParseSkillFile(read);
            var digest = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(parsed.Body))).ToLowerInvariant();
            return new SkillBody(parsed.Name, parsed.Description, parsed.Body, digest, skillFile, parsed.Requires);
        }

        return null;
    }

    private static string? ReadSkillFile(string path)
    {
        try
        {
            using var stream = HardLinkGuard.OpenRead(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, path);
            var bytes = new byte[64 * 1024 + 1];
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

            if (count > 64 * 1024)
            {
                return null;
            }

            var content = StrictUtf8.GetString(bytes, 0, count)
                .TrimStart('﻿')
                .Normalize(NormalizationForm.FormC)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd();
            return content.Contains('\0') ? null : content;
        }
        catch (DecoderFallbackException)
        {
            return null;
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

    private static (string Name, string Description, string Body, IReadOnlyList<string> Requires) ParseSkillFile(string content)
    {
        var lines = content.Split('\n');
        string? name = null;
        string? description = null;
        IReadOnlyList<string>? requires = null;
        var body = content;
        if (lines.Length >= 3 && string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            var closing = -1;
            for (var index = 1; index < lines.Length; index++)
            {
                if (string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
                {
                    closing = index;
                    break;
                }
            }

            if (closing > 0)
            {
                for (var index = 1; index < closing; index++)
                {
                    var separator = lines[index].IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = lines[index][..separator].Trim().ToLowerInvariant();
                    var value = Unquote(lines[index][(separator + 1)..].Trim());
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(key, "name", StringComparison.Ordinal))
                    {
                        name ??= value;
                    }
                    else if (string.Equals(key, "description", StringComparison.Ordinal))
                    {
                        description ??= value;
                    }
                    else if (string.Equals(key, "requires", StringComparison.Ordinal))
                    {
                        requires ??= SkillRequirements.Parse(value);
                    }
                }

                body = string.Join('\n', lines[(closing + 1)..]).TrimStart('\n').TrimEnd();
            }
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            body = content.Trim();
        }

        name = string.IsNullOrWhiteSpace(name) ? "skill" : name.Trim();
        description = string.IsNullOrWhiteSpace(description) ? "Workspace workflow guidance." : description.Trim();
        return (name, description, body, requires ?? Array.Empty<string>());
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
}

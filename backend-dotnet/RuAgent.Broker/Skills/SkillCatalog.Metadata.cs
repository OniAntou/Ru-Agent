using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public sealed partial class SkillCatalog
{
    private static (SkillMetadata Metadata, string Revision)? ReadMetadata(string path) => ReadMetadata(path, out _);

    private static (SkillMetadata Metadata, string Revision)? ReadMetadata(string path, out string? errorCode)
    {
        errorCode = null;
        try
        {
            var info = new FileInfo(path); if (!info.Exists || HasReparseAncestor(path)) return null;
            using var stream = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, path);
            Span<byte> prefix = stackalloc byte[7];
            var prefixCount = stream.Read(prefix);
            var prefixOffset = prefixCount >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF ? 3 : 0;
            stream.Position = prefixOffset;
            var folderId = Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill";
            if (prefixCount < prefixOffset + 4 || prefix[prefixOffset] != (byte)'-' || prefix[prefixOffset + 1] != (byte)'-' || prefix[prefixOffset + 2] != (byte)'-'
                || (prefix[prefixOffset + 3] != (byte)'\n' && prefix[prefixOffset + 3] != (byte)'\r'))
            {
                stream.Position = prefixOffset;
                var firstLine = new byte[2048]; var firstLineLength = 0;
                while (firstLineLength < firstLine.Length)
                {
                    var value = stream.ReadByte(); if (value < 0 || value == '\n') break;
                    firstLine[firstLineLength++] = (byte)value;
                }
                // The byte budget may end inside a valid UTF-8 code point. Defer
                // only that trailing fragment; full body decoding still validates the file.
                Span<char> characters = stackalloc char[2048];
                StrictUtf8.GetDecoder().Convert(firstLine.AsSpan(0, firstLineLength), characters,
                    flush: firstLineLength < firstLine.Length, out _, out var charactersUsed, out _);
                var firstLineText = new string(characters[..charactersUsed]).TrimStart('\uFEFF').TrimEnd('\r');
                if (firstLineText.Trim() != "---")
                {
                    var plain = SkillMetadataParser.Parse(firstLineText, folderId) with { Body = string.Empty, Diagnostics = ["missing_frontmatter"] };
                    return (plain, Revision(info, plain));
                }
                stream.Position = prefixOffset;
            }

            var bytes = new byte[SkillMetadataParser.MaxFrontmatterBytes + 1]; var count = 0; var closing = false; var lineNumber = 0; var line = new List<byte>(4);
            while (count < bytes.Length)
            {
                var value = stream.ReadByte(); if (value < 0) break;
                bytes[count++] = (byte)value;
                line.Add((byte)value);
                if (value == '\n')
                {
                    var end = line.Count - 1;
                    var start = 0;
                    while (end > 0 && line[end - 1] is (byte)'\r' or (byte)' ' or (byte)'\t') end--;
                    while (start < end && line[start] is (byte)' ' or (byte)'\t') start++;
                    var delimiter = end - start == 3 && line[start] == '-' && line[start + 1] == '-' && line[start + 2] == '-';
                    closing = lineNumber > 0 && delimiter;
                    lineNumber++;
                    line.Clear();
                }
                if (closing) break;
            }
            if (count > SkillMetadataParser.MaxFrontmatterBytes || !closing) return null;
            var head = StrictUtf8.GetString(bytes, 0, count).TrimStart('\uFEFF');
            var parsed = SkillMetadataParser.Parse(head, folderId);
            return (parsed, Revision(info, parsed));
        }
        catch (DecoderFallbackException) { errorCode = "invalid_encoding"; return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string Revision(FileInfo info, SkillMetadata metadata)
    {
        var value = string.Join("\0", new[]
        {
            info.Length.ToString(CultureInfo.InvariantCulture), info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), metadata.Id,
            metadata.Name, metadata.Description, metadata.Compatibility ?? string.Empty, string.Join("\0", metadata.Requires),
            metadata.ModelInvocable.ToString(), metadata.UserInvocable.ToString(), string.Join("\0", metadata.Diagnostics),
        });
        return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool HasReparseAncestor(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            if (File.Exists(current)) current = Path.GetDirectoryName(current)!;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && IsReparse(current)) return true;
                var parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent ?? string.Empty;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }
}

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public sealed partial class SkillPackageInstaller
{
    private async Task<byte[]> DownloadArchiveAsync(PinnedSkillSource source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri($"https://codeload.github.com/{source.Owner}/{source.Repository}/zip/{source.Commit}"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw Failure("skill_source_redirect", "The pinned package source redirected; redirects are not followed.");
            if (response.StatusCode != HttpStatusCode.OK)
                throw Failure("skill_source_unavailable", $"The pinned package source returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
                throw Failure("skill_archive_too_large", "The compressed skill archive exceeds the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseReadBytes)
                    throw Failure("skill_archive_too_large", "The compressed skill archive exceeds the size limit.");
                output.Write(buffer, 0, read);
            }
            if (output.Length > MaxArchiveBytes)
                throw Failure("skill_archive_too_large", "The compressed skill archive exceeds the size limit.");
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Failure("skill_source_timeout", "The pinned package source did not respond before the download deadline."); }
    }

    private List<StagedFile> ExtractSelectedPackage(byte[] archiveBytes, PinnedSkillSource source, string packageDirectory)
    {
        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxArchiveEntries)
            throw Failure("skill_archive_too_large", "The GitHub archive contains too many entries.");
        var rootPrefix = source.Repository + "-" + source.Commit + "/";
        var selectedPrefix = rootPrefix + source.Subdirectory + "/";
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedEntries = new List<(ZipArchiveEntry Entry, string Relative)>();
        long archiveExpandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName;
            var isDirectory = raw.EndsWith("/", StringComparison.Ordinal);
            var path = isDirectory ? raw[..^1] : raw;
            ValidateArchivePath(path);
            if (!seenPaths.Add(path)) throw Failure("skill_archive_invalid", "The archive contains duplicate paths.");
            if ((((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
                throw Failure("skill_archive_invalid", "Symbolic links are not accepted in skill archives.");
            if (entry.Length < 0 || entry.Length > MaxFileBytes)
                throw Failure("skill_archive_too_large", "A skill package file exceeds the size limit.");
            if (isDirectory) continue;
            archiveExpandedBytes += entry.Length;
            if (archiveExpandedBytes > MaxExpandedBytes)
                throw Failure("skill_archive_too_large", "The expanded skill archive exceeds the size limit.");
            if (path.StartsWith(selectedPrefix, StringComparison.Ordinal))
            {
                var relative = path[selectedPrefix.Length..];
                if (relative.Length == 0) continue;
                if (string.Equals(relative, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw Failure("skill_archive_invalid", "The package uses a reserved installer metadata filename.");
                selectedEntries.Add((entry, relative));
            }
        }
        if (selectedEntries.Count == 0 || selectedEntries.Count > MaxFiles)
            throw Failure(selectedEntries.Count == 0 ? "skill_package_not_found" : "skill_archive_too_large",
                selectedEntries.Count == 0 ? "The selected folder does not contain a skill package." : "The skill package contains too many files.");

        Directory.CreateDirectory(packageDirectory);
        long expandedTotal = 0;
        var result = new List<StagedFile>(selectedEntries.Count);
        foreach (var (entry, relative) in selectedEntries)
        {
            var destination = ContainedPath(packageDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(destination)!;
            CreateSafeDirectories(packageDirectory, parent);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                length += read;
                expandedTotal += read;
                if (length > MaxFileBytes || expandedTotal > MaxExpandedBytes)
                    throw Failure("skill_archive_too_large", "The expanded skill package exceeds the size limit.");
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            if (length != entry.Length) throw Failure("skill_archive_invalid", "An archive entry length did not match its contents.");
            result.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }
        if (!result.Any(file => string.Equals(file.RelativePath, "SKILL.md", StringComparison.Ordinal)))
            throw Failure("skill_manifest_missing", "The selected package must contain SKILL.md at its root.");
        return result;
    }

    private static SkillMetadata ParseSkillFile(string id, string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > 64 * 1024) throw Failure("skill_manifest_invalid", "SKILL.md exceeds the loader's body size limit.");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        var text = StrictUtf8.GetString(bytes);
        if (!text.TrimStart('\uFEFF').StartsWith("---\n", StringComparison.Ordinal)
            && !text.TrimStart('\uFEFF').StartsWith("---\r\n", StringComparison.Ordinal))
            throw Failure("skill_manifest_invalid", "SKILL.md must contain supported YAML frontmatter.");
        var metadata = SkillMetadataParser.Parse(text, id);
        if (metadata.Diagnostics.Contains("invalid_frontmatter", StringComparer.Ordinal)
            || metadata.Diagnostics.Contains("frontmatter_unclosed", StringComparer.Ordinal)
            || metadata.Diagnostics.Contains("frontmatter_oversize", StringComparer.Ordinal))
            throw Failure("skill_manifest_invalid", "SKILL.md frontmatter is invalid or exceeds the parser limit.");
        return metadata;
    }

}

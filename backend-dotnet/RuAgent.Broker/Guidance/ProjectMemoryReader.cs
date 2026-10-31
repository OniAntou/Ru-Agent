using System.Text;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Guidance;

public sealed record ProjectMemoryRead(string? Content, string? Diagnostic);

/// <summary>A bounded, data-only index shared by conversations in one workspace.</summary>
public static class ProjectMemoryReader
{
    public const string RelativePath = ".ru/memory/MEMORY.md";
    public const int MaximumBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ProjectMemoryRead> ReadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = new CanonicalPathResolver(workspaceRoot).Resolve(RelativePath);
            if (!target.IsInsideWorkspace || ProtectedPathCatalog.IsProtected(target.CanonicalPath))
                return new(null, "memory_path_unavailable");
            if (!File.Exists(target.CanonicalPath)) return new(null, null);
            if (HardLinkGuard.HasMultipleLinks(target.CanonicalPath)) return new(null, "memory_linked_file");
            await using var stream = HardLinkGuard.OpenRead(target.CanonicalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous);
            HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, target.CanonicalPath);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumBytes) return new(null, "memory_index_oversize");
            var content = StrictUtf8.GetString(bytes, 0, count).TrimStart('\uFEFF')
                .Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Trim();
            if (content.Contains('\0')) return new(null, "memory_index_invalid");
            return new(string.IsNullOrWhiteSpace(content) ? null : content, null);
        }
        catch (DecoderFallbackException) { return new(null, "memory_index_invalid_encoding"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        {
            return new(null, "memory_index_unreadable");
        }
    }
}

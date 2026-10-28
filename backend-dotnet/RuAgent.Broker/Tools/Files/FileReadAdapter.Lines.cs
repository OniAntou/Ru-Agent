using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed partial class FileReadAdapter
{
    public const int MaximumLineOutputBytes = 32 * 1024;

    public async ValueTask<JsonObject> ExecuteLinesAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = GetRequiredString(arguments, "path");
        if (path.Length > FileToolLimits.MaxPathLength) throw new ArgumentException("path_too_long");
        var first = LineArgument(arguments, "start_line", 1, 1_000_000);
        var count = LineArgument(arguments, "line_count", 200, 1000);
        var target = _resolver.Resolve(path);
        if (target.IsProtected) throw new UnauthorizedAccessException("protected_path");
        if (target.IsOutsideWorkspace) throw new UnauthorizedAccessException("outside_workspace");
        // A bounded snapshot avoids returning pages assembled from different file revisions.
        using var stream = HardLinkGuard.OpenRead(target.CanonicalPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > FileEditAdapter.MaxFileBytes) throw new ArgumentException("read_line_scan_limit");
            bytes.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        string text;
        try { text = StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException error) { throw new ArgumentException("invalid_utf8", error); }
        var position = 0;
        var line = 1;
        while (line < first && position < text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var end = text.IndexOf('\n', position);
            position = end < 0 ? text.Length : end + 1;
            line++;
        }
        var content = new StringBuilder();
        var output = new JsonObject
        {
            ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, target.CanonicalPath),
            ["start_line"] = first, ["line_count"] = 0, ["content"] = "",
            ["has_more"] = false, ["next_line"] = null, ["cursor_unit"] = "line_1_based",
            ["sha256"] = FileHashService.ComputeBytesHash(bytes.ToArray()),
        };
        var returned = 0;
        while (position < text.Length && returned < count)
        {
            ct.ThrowIfCancellationRequested();
            var end = text.IndexOf('\n', position);
            end = end < 0 ? text.Length : end + 1;
            var length = content.Length;
            content.Append(text.AsSpan(position, end - position));
            output["content"] = content.ToString();
            // Reserve enough JSON space for final counters and cursor.
            if (Encoding.UTF8.GetByteCount(output.ToJsonString()) > MaximumLineOutputBytes - 128)
            {
                if (returned == 0) throw new ArgumentException("read_line_too_long");
                content.Length = length;
                output["content"] = content.ToString();
                break;
            }
            position = end;
            returned++;
        }
        output["line_count"] = returned;
        output["has_more"] = position < text.Length;
        if (position < text.Length) output["next_line"] = first + returned;
        return output;
    }

    private static int LineArgument(JsonObject arguments, string name, int fallback, int maximum)
    {
        if (!arguments.ContainsKey(name)) return fallback;
        if (arguments[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 1 && number <= maximum)
            return number;
        throw new ArgumentException("invalid_" + name);
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileReadAdapter
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileReadAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = GetRequiredString(arguments, "path");
        if (path.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        var offset = 0;
        if (arguments.TryGetPropertyValue("offset", out var offNode) && offNode is not null)
        {
            if (offNode is JsonValue ov && ov.TryGetValue<int>(out var ovInt))
            {
                if (ovInt < 0)
                {
                    throw new ArgumentException("invalid_offset");
                }

                offset = ovInt;
            }
            else if (offNode is JsonValue ovLong && ovLong.TryGetValue<long>(out var ovLongVal))
            {
                if (ovLongVal < 0 || ovLongVal > int.MaxValue)
                {
                    throw new ArgumentException("invalid_offset");
                }

                offset = (int)ovLongVal;
            }
            else
            {
                throw new ArgumentException("invalid_offset");
            }
        }

        var limit = FileToolLimits.MaxReadBytes;
        if (arguments.TryGetPropertyValue("limit", out var limNode) && limNode is not null)
        {
            if (limNode is JsonValue lv && lv.TryGetValue<int>(out var lvInt))
            {
                if (lvInt < 1 || lvInt > FileToolLimits.MaxReadBytes)
                {
                    throw new ArgumentException("invalid_limit");
                }

                limit = lvInt;
            }
            else if (limNode is JsonValue lvLong && lvLong.TryGetValue<long>(out var lvLongVal))
            {
                if (lvLongVal < 1 || lvLongVal > FileToolLimits.MaxReadBytes)
                {
                    throw new ArgumentException("invalid_limit");
                }

                limit = (int)lvLongVal;
            }
            else
            {
                throw new ArgumentException("invalid_limit");
            }
        }

        var resolved = ResolveChecked(path);
        var canonical = resolved.CanonicalPath;

        if (!File.Exists(canonical))
        {
            if (Directory.Exists(canonical))
            {
                throw new IOException("is_directory");
            }

            throw new FileNotFoundException("file_not_found");
        }

        var info = new FileInfo(canonical);
        long fileLength = info.Length;

        HardLinkGuard.RequireSingleLink(canonical);
        var sw = Stopwatch.StartNew();
        var buffer = new byte[limit];
        var observedFileLength = fileLength;
        int bytesRead = 0;
        using (var stream = HardLinkGuard.OpenRead(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            while (bytesRead < limit)
            {
                if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
                {
                    break;
                }

                int toRead = limit - bytesRead;
                int n = stream.Read(buffer, bytesRead, toRead);
                if (n == 0)
                {
                    break;
                }

                bytesRead += n;
            }

            // FileInfo.Length is only a point-in-time snapshot. If another
            // process appends while we read, use the largest length observed so
            // the caller cannot mistake a moving file for a complete read.
            try
            {
                observedFileLength = Math.Max(observedFileLength, stream.Length);
            }
            catch (IOException)
            {
                // Preserve the initial snapshot when the handle cannot report
                // its final length; the read itself remains valid.
            }
        }

        // Keep the arithmetic in Int64: a valid Int32 offset near EOF can
        // otherwise wrap when the requested window crosses 2 GiB, incorrectly
        // reporting a complete read.
        bool isMore = ((long)offset + bytesRead) < observedFileLength;
        // Check time truncation
        bool timeTruncated = sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs;

        int validLength = bytesRead;
        bool utfTruncated = false;
        if (bytesRead > 0)
        {
            validLength = GetValidUtf8Length(buffer, bytesRead);
            if (validLength < bytesRead)
            {
                utfTruncated = true;
            }
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(buffer, 0, validLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }

        bool truncated = isMore || utfTruncated || timeTruncated || validLength < bytesRead;
        // If we hit time limit, truncated true
        if (timeTruncated)
        {
            truncated = true;
        }

        var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, canonical);
        return new JsonObject
        {
            ["path"] = relative,
            ["content"] = content,
            ["truncated"] = truncated,
            ["bytes_read"] = validLength,
            ["total_bytes"] = observedFileLength,
            ["offset"] = offset,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
            ["sha256"] = FileHashService.GetCurrentHash(canonical),
        };
    }

    private ResolvedPath ResolveChecked(string input)
    {
        try
        {
            var resolved = _resolver.Resolve(input);
            if (resolved.IsProtected)
            {
                throw new UnauthorizedAccessException("protected_path");
            }

            if (resolved.IsOutsideWorkspace)
            {
                throw new UnauthorizedAccessException("outside_workspace");
            }

            return resolved;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("invalid_path", ex);
        }
    }

    private static int GetValidUtf8Length(byte[] buffer, int length)
    {
        if (length == 0)
        {
            return 0;
        }

        // Walk backwards to find start of last UTF-8 sequence
        int i = length - 1;
        // If last byte is ASCII, valid
        if ((buffer[i] & 0x80) == 0)
        {
            return length;
        }

        // Count trailing continuation bytes
        int contCount = 0;
        while (i >= 0 && (buffer[i] & 0xC0) == 0x80)
        {
            contCount++;
            i--;
            if (contCount >= 3)
            {
                break;
            }
        }

        if (i < 0)
        {
            // All continuation bytes without start, truncate them
            return length - contCount;
        }

        byte start = buffer[i];
        int expected = 0;
        if ((start & 0xE0) == 0xC0)
        {
            expected = 2;
        }
        else if ((start & 0xF0) == 0xE0)
        {
            expected = 3;
        }
        else if ((start & 0xF8) == 0xF0)
        {
            expected = 4;
        }
        else
        {
            // Invalid start byte, treat as truncated before it
            return i;
        }

        int actual = contCount + 1;
        if (actual < expected)
        {
            // Incomplete sequence
            return i;
        }

        if (actual > expected)
        {
            // Should not happen with valid UTF-8, but if actual > expected, the extra bytes are part of next char? Then sequence is valid
            return length;
        }

        return length;
    }

    private static string GetRequiredString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Hosting;

public static class ChildProcessPolicy
{
    private const long DefaultMaxLineCount = 1_000_000;
    private const long DefaultMaxByteCount = 64L * 1024 * 1024;

    private static readonly string[] EnvironmentAllowlist =
    [
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP",
        "LOCALAPPDATA",
        "APPDATA",
        "USERPROFILE",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_ARCHITEW6432",
        "NUMBER_OF_PROCESSORS",
        "PATH",
        "PATHEXT",
        "COMSPEC",
    ];

    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(
        ["python-runtime-worker", "ru-agent-broker"],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> Streams = new HashSet<string>(
        ["stdout", "stderr"],
        StringComparer.Ordinal);

    public static ProcessStartInfo CreateStartInfo(WorkerBrokerProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Roles.Contains(spec.Role))
        {
            throw new ArgumentException("invalid_backend_process_role", nameof(spec));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Clear();
        foreach (var key in EnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[key] = value;
            }
        }

        foreach (var variable in spec.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    public static async Task DrainToSummaryAsync(
        TextReader reader,
        string path,
        string role,
        string stream,
        long maxLineCount = DefaultMaxLineCount,
        long maxByteCount = DefaultMaxByteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Roles.Contains(role) || !Streams.Contains(stream))
        {
            throw new ArgumentException("invalid_bootstrap_summary_identity");
        }
        if (maxLineCount < 1 || maxByteCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineCount));
        }

        var buffer = new char[4096];
        var encoder = Encoding.UTF8.GetEncoder();
        long lineCount = 0;
        long byteCount = 0;
        var hasContentSinceNewline = false;
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var encodedBytes = encoder.GetByteCount(buffer, 0, count, flush: false);
            byteCount = SaturatingAdd(byteCount, encodedBytes, maxByteCount, ref truncated);
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n')
                {
                    lineCount = SaturatingAdd(lineCount, 1, maxLineCount, ref truncated);
                    hasContentSinceNewline = false;
                }
                else
                {
                    hasContentSinceNewline = true;
                }
            }
        }

        byteCount = SaturatingAdd(
            byteCount,
            encoder.GetByteCount([], 0, 0, flush: true),
            maxByteCount,
            ref truncated);
        if (hasContentSinceNewline)
        {
            lineCount = SaturatingAdd(lineCount, 1, maxLineCount, ref truncated);
        }

        var summary = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["role"] = role,
            ["stream"] = stream,
            ["line_count"] = lineCount,
            ["byte_count"] = byteCount,
            ["truncated"] = truncated,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        await writer.WriteLineAsync(summary.ToJsonString().AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static long SaturatingAdd(
        long current,
        long increment,
        long maximum,
        ref bool truncated)
    {
        if (increment > maximum - current)
        {
            truncated = true;
            return maximum;
        }

        return current + increment;
    }
}

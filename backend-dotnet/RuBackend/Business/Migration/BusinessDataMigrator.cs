using System.Security.Cryptography;
using System.Text.Json;

namespace RuBackend.Business.Migration;

public sealed record BusinessDataMigrationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> MigratedFiles);

public sealed class BusinessDataMigrator
{
    private static readonly string[] RootFiles =
        ["settings.json", "history.json", "activity_log.jsonl", "pyvts_token.txt"];

    private const long MaximumRootFileBytes = 64L * 1024 * 1024;
    private const long MaximumSessionBytes = 8L * 1024 * 1024;
    private const long MaximumTokenBytes = 64 * 1024;
    private const int MaximumSessionCount = 10_000;

    private readonly DataPaths _legacy;
    private readonly DataPaths _canonical;

    public BusinessDataMigrator(DataPaths legacy, DataPaths canonical)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _canonical = canonical ?? throw new ArgumentNullException(nameof(canonical));
    }

    public async Task<BusinessDataMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_legacy.SessionDirectory) && !File.Exists(_legacy.SettingsFile))
        {
            return new BusinessDataMigrationResult(false, "Legacy directory is absent or empty.", []);
        }

        var businessRoot = Path.GetDirectoryName(_canonical.SessionDirectory)!
            ?? throw new InvalidOperationException("Canonical directory has no parent.");
        var canonicalParent = Path.GetDirectoryName(businessRoot)
            ?? throw new InvalidOperationException("Canonical parent directory is missing.");
        var staging = Path.Combine(
            canonicalParent,
            $".ru-migration-{Guid.NewGuid():N}");

        var migrated = new List<string>();
        try
        {
            if (Directory.Exists(businessRoot)
                && Directory.EnumerateFileSystemEntries(businessRoot).Any())
            {
                return new BusinessDataMigrationResult(
                    false,
                    "Canonical directory is not empty; migration aborted.",
                    []);
            }

            Directory.CreateDirectory(staging);
            var stagingSessions = Path.Combine(staging, "sessions");
            Directory.CreateDirectory(stagingSessions);

            foreach (var fileName in RootFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var legacyRoot = Path.GetDirectoryName(_legacy.SettingsFile)!;
                var source = Path.Combine(legacyRoot, fileName);
                if (!File.Exists(source))
                {
                    continue;
                }

                var maximum = fileName == "pyvts_token.txt" ? MaximumTokenBytes : MaximumRootFileBytes;
                var target = Path.Combine(staging, fileName);
                await CopyValidatedAsync(source, target, maximum, isJsonLines: fileName == "activity_log.jsonl", cancellationToken)
                    .ConfigureAwait(false);
                migrated.Add(fileName);
            }

            var legacySessions = _legacy.SessionDirectory;
            if (Directory.Exists(legacySessions))
            {
                if (Directory.EnumerateDirectories(legacySessions).Any())
                {
                    return new BusinessDataMigrationResult(
                        false,
                        "Session directory contains unexpected nesting; migration aborted.",
                        []);
                }

                var sessionFiles = Directory.EnumerateFiles(legacySessions, "*.json")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                if (sessionFiles.Length > MaximumSessionCount)
                {
                    return new BusinessDataMigrationResult(false, "Session count exceeds the migration bound.", []);
                }

                foreach (var session in sessionFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(session);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return new BusinessDataMigrationResult(false, "Session file is a link; migration aborted.", []);
                    }

                    var target = Path.Combine(stagingSessions, Path.GetFileName(session));
                    await CopyValidatedAsync(session, target, MaximumSessionBytes, isJsonLines: false, cancellationToken)
                        .ConfigureAwait(false);
                    migrated.Add(Path.Combine("sessions", Path.GetFileName(session)));
                }
            }

            if (Directory.Exists(businessRoot))
            {
                Directory.Delete(businessRoot, recursive: true);
            }

            Directory.Move(staging, businessRoot);
            return new BusinessDataMigrationResult(true, "Business data migrated to the canonical location.", migrated);
        }
        catch (InvalidOperationException exception)
        {
            return new BusinessDataMigrationResult(false, exception.Message, []);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task CopyValidatedAsync(
        string source,
        string target,
        long maximumBytes,
        bool isJsonLines,
        CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Legacy data contains a link; migration aborted.");
        }

        var sourceBytes = new FileInfo(source).Length;
        if (sourceBytes > maximumBytes)
        {
            throw new InvalidOperationException("Legacy data file exceeds the size bound.");
        }

        var content = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        if (isJsonLines || !Path.GetFileName(source).Equals("pyvts_token.txt", StringComparison.Ordinal))
        {
            ValidateContent(content, isJsonLines, Path.GetFileName(source));
        }

        await File.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
        var expectedHash = SHA256.HashData(content);
        var stagedHash = SHA256.HashData(
            await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false));
        if (!expectedHash.AsSpan().SequenceEqual(stagedHash.AsSpan()))
        {
            throw new InvalidOperationException("Staged copy failed its SHA-256 verification.");
        }
    }

    private static void ValidateContent(byte[] content, bool isJsonLines, string fileName)
    {
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Legacy data file is empty.");
        }

        var text = System.Text.Encoding.UTF8.GetString(content).TrimStart('\uFEFF');
        try
        {
            if (isJsonLines)
            {
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r').Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    _ = JsonDocument.Parse(trimmed);
                }
            }
            else
            {
                _ = JsonDocument.Parse(text);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Legacy data is not valid JSON: {fileName}.",
                exception);
        }
    }
}
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Sessions;

public interface IHistoryStore
{
    Task<IReadOnlyList<SessionMessage>> ReadLegacyAsync(CancellationToken cancellationToken);

    Task<string?> MigrateToSessionAsync(CancellationToken cancellationToken);
}

public sealed class HistoryStore : IHistoryStore
{
    private const long MaximumLegacyFileBytes = 16 * 1024 * 1024;
    private const int MaximumManifestBytes = 1 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _historyFile;
    private readonly string _manifestFile;
    private readonly ISessionStore _sessions;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HistoryStore(
        DataPaths paths,
        ISessionStore sessions,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _historyFile = paths.HistoryFile;
        _manifestFile = paths.HistoryFile + ".migrations.json";
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SessionMessage>> ReadLegacyAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLegacyUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> MigrateToSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_historyFile) ||
                (await _sessions.ListAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                return null;
            }

            byte[] sourceBytes;
            try
            {
                if (new FileInfo(_historyFile).Length > MaximumLegacyFileBytes)
                {
                    return null;
                }

                sourceBytes = await ReadBoundedBytesAsync(_historyFile, MaximumLegacyFileBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (InvalidDataException)
            {
                return null;
            }

            var messages = ParseLegacy(sourceBytes);
            if (messages.Count == 0)
            {
                return null;
            }

            var sessionId = await _sessions.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (sessionId is null)
            {
                return null;
            }

            if (!await _sessions.AppendAsync(sessionId, messages, cancellationToken)
                    .ConfigureAwait(false))
            {
                await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var verified = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (verified is null || !verified.Messages.SequenceEqual(messages))
            {
                await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var backup = BuildBackupPath();
            try
            {
                File.Copy(_historyFile, backup, overwrite: false);
                var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
                manifest.Add(new JsonObject
                {
                    ["source_sha256"] = Convert.ToHexString(SHA256.HashData(sourceBytes))
                        .ToLowerInvariant(),
                    ["session_id"] = sessionId,
                    ["backup_file"] = backup,
                    ["migrated_at"] = _clock.GetUtcNow().ToUniversalTime(),
                });
                await WriteJsonAtomicAsync(_manifestFile, manifest, cancellationToken)
                    .ConfigureAwait(false);
                File.Delete(_historyFile);
                return sessionId;
            }
            catch (IOException)
            {
                File.Delete(backup);
                await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                File.Delete(backup);
                await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SessionMessage>> ReadLegacyUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyFile))
        {
            return [];
        }

        try
        {
            if (new FileInfo(_historyFile).Length > MaximumLegacyFileBytes)
            {
                return [];
            }

            var bytes = await ReadBoundedBytesAsync(_historyFile, MaximumLegacyFileBytes, cancellationToken)
                .ConfigureAwait(false);
            return ParseLegacy(bytes);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SessionMessage> ParseLegacy(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(bytes) is not JsonArray items)
            {
                return [];
            }

            var messages = new List<SessionMessage>();
            foreach (var item in items)
            {
                if (item is not JsonObject entry ||
                    entry["role"] is not JsonValue roleValue ||
                    !roleValue.TryGetValue<string>(out var role) ||
                    (role != "user" && role != "ai") ||
                    entry["content"] is not JsonValue contentValue ||
                    !contentValue.TryGetValue<string>(out var content))
                {
                    continue;
                }

                messages.Add(new SessionMessage(role, content));
            }

            return messages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<JsonArray> ReadManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestFile))
        {
            return [];
        }

        try
        {
            var bytes = await ReadBoundedBytesAsync(_manifestFile, MaximumManifestBytes, cancellationToken)
                .ConfigureAwait(false);
            return JsonNode.Parse(bytes) as JsonArray ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 81920));
        var buffer = new byte[(int)Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("history_file_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private string BuildBackupPath()
    {
        var stamp = _clock.GetUtcNow().ToUniversalTime().ToString("yyyyMMddHHmmssfff");
        var candidate = $"{_historyFile}.{stamp}.bak";
        return File.Exists(candidate)
            ? $"{_historyFile}.{stamp}.{Guid.NewGuid():N}.bak"
            : candidate;
    }

    private static async Task WriteJsonAtomicAsync(
        string target,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("JSON path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

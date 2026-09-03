using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    public async Task<SessionAppendOutcome> AppendIfAbsentAsync(
        string sessionId,
        SessionMessage message,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return SessionAppendOutcome.SessionNotFound;
        }

        if (!IsValidMessage(message)
            || message.SourceMessageId is null
            || message.SourceRunId is null)
        {
            return SessionAppendOutcome.InvalidMessage;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadUnlockedAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return SessionAppendOutcome.SessionNotFound;
            }

            var ledger = BuildSourceLedger(current);
            if (ledger.TryGetValue(message.SourceMessageId, out var existingFingerprint))
            {
                return string.Equals(
                        existingFingerprint,
                        SourceFingerprint(message),
                        StringComparison.Ordinal)
                    ? SessionAppendOutcome.AlreadyPresent
                    : SessionAppendOutcome.InvalidMessage;
            }

            await AppendUnlockedAsync(current, [message], cancellationToken)
                .ConfigureAwait(false);
            return SessionAppendOutcome.Appended;
        }
        catch (IOException)
        {
            return SessionAppendOutcome.TransientFailure;
        }
        catch (UnauthorizedAccessException)
        {
            return SessionAppendOutcome.TransientFailure;
        }
        catch (InvalidDataException)
        {
            return SessionAppendOutcome.TransientFailure;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AppendUnlockedAsync(
        SessionDocument current,
        IReadOnlyList<SessionMessage> messages,
        CancellationToken cancellationToken)
    {
        var merged = current.Messages.Concat(messages).ToArray();
        long contentBytes = 0;
        foreach (var message in merged)
        {
            contentBytes += Encoding.UTF8.GetByteCount(message.Content);
            if (contentBytes > MaximumSessionFileBytes)
            {
                // Reject before JsonSerializer allocates a potentially much
                // larger document for a session that cannot be persisted.
                throw new InvalidDataException("session_file_too_large");
            }
        }

        var ledger = BuildSourceLedger(current);
        foreach (var message in messages.Where(message => message.SourceMessageId is not null))
        {
            var fingerprint = SourceFingerprint(message);
            if (ledger.TryGetValue(message.SourceMessageId!, out var existing)
                && !string.Equals(existing, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("session_source_ledger_conflict");
            }

            ledger[message.SourceMessageId!] = fingerprint;
        }

        var title = string.IsNullOrWhiteSpace(current.Title) || current.Title == DefaultTitle
            ? TitleFromMessages(merged)
            : current.Title;
        var updated = current with
        {
            Title = title,
            UpdatedAt = _clock.GetUtcNow().ToUniversalTime(),
            Messages = merged.TakeLast(MaximumMessages).ToArray(),
            SourceMessageLedger = ledger.Count == 0 ? null : ledger,
        };
        await WriteUnlockedAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidSourceId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '_'
                or '-'
                or '.'
                or ':');

    private static Dictionary<string, string> BuildSourceLedger(SessionDocument document)
    {
        var ledger = document.SourceMessageLedger is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                document.SourceMessageLedger,
                StringComparer.Ordinal);
        foreach (var message in document.Messages.Where(message =>
                     message.SourceMessageId is not null))
        {
            ledger.TryAdd(message.SourceMessageId!, SourceFingerprint(message));
        }

        return ledger;
    }

    private static bool TryReadSourceLedger(
        JsonObject root,
        IReadOnlyList<SessionMessage> messages,
        out Dictionary<string, string> ledger)
    {
        ledger = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["source_message_ledger"] is { } ledgerNode)
        {
            if (ledgerNode is not JsonObject ledgerObject)
            {
                return false;
            }

            foreach (var item in ledgerObject)
            {
                if (!IsValidSourceId(item.Key)
                    || item.Value is not JsonValue value
                    || !value.TryGetValue<string>(out var fingerprint)
                    || !IsSha256(fingerprint))
                {
                    return false;
                }

                ledger[item.Key] = fingerprint;
            }
        }

        foreach (var message in messages.Where(message => message.SourceMessageId is not null))
        {
            var fingerprint = SourceFingerprint(message);
            if (ledger.TryGetValue(message.SourceMessageId!, out var stored)
                && !string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            ledger[message.SourceMessageId!] = fingerprint;
        }

        return true;
    }

    private static string SourceFingerprint(SessionMessage message)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            message.Role,
            message.Content,
            message.SourceRunId!,
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

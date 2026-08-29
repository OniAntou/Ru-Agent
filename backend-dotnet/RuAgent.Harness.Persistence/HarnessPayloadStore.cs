using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence;

public sealed record ProtectedHarnessPayload(
    HarnessPayloadIntent Intent,
    EncryptedHarnessPayload Encrypted);

public sealed record HarnessPayloadContent(
    RunId RunId,
    PayloadId PayloadId,
    HarnessPayloadKind Kind,
    HarnessPayloadAudience Audience,
    HarnessPayloadEncoding Encoding,
    ReadOnlyMemory<byte> Plaintext);

public sealed record HarnessBaseContextEntry
{
    public HarnessBaseContextEntry(
        PayloadId payloadId,
        string sourceId,
        long sequence,
        ModelContextRole role,
        string content)
    {
        _ = payloadId.Value;
        if (!IsSourceId(sourceId))
        {
            throw new ArgumentException("Base context source ID is invalid.", nameof(sourceId));
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (role is not ModelContextRole.System
            and not ModelContextRole.User
            and not ModelContextRole.Assistant)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentNullException.ThrowIfNull(content);
        PayloadId = payloadId;
        SourceId = sourceId;
        Sequence = sequence;
        Role = role;
        Content = content;
    }

    public PayloadId PayloadId { get; }

    public string SourceId { get; }

    public long Sequence { get; }

    public ModelContextRole Role { get; }

    public string Content { get; }

    private static bool IsSourceId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '-'
                or '_'
                or '.'
                or ':');
}

public enum BaseContextCaptureOutcome
{
    Captured,
    AlreadyCaptured,
}

public sealed partial class HarnessPayloadStore
{
    private static readonly IReadOnlyDictionary<HarnessPayloadKind, PayloadPolicy> Policies =
        new Dictionary<HarnessPayloadKind, PayloadPolicy>
        {
            [HarnessPayloadKind.UserPrompt] = new(
                1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.AssistantText] = new(
                1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.ToolArguments] = new(
                ModelToolCall.MaximumArgumentBytes,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Json]),
            [HarnessPayloadKind.ToolOutput] = new(
                4 * 1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text, HarnessPayloadEncoding.Json]),
            [HarnessPayloadKind.Diff] = new(
                4 * 1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text, HarnessPayloadEncoding.Json]),
            [HarnessPayloadKind.ContinuationSummary] = new(
                1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.RecoveryOpaque] = new(
                HarnessPayloadIntent.MaximumByteCap,
                HarnessPayloadAudience.InternalOnly,
                [HarnessPayloadEncoding.Opaque]),
            [HarnessPayloadKind.AssistantStep] = new(
                2 * 1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Json]),
            [HarnessPayloadKind.ModelContextBase] = new(
                4 * 1024 * 1024,
                HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.ModelContextSummary] = new(
                4 * 1024 * 1024,
                HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.PromptReceipt] = new(
                16 * 1024,
                HarnessPayloadAudience.InternalOnly,
                [HarnessPayloadEncoding.Json]),
        };

    private readonly HarnessPayloadCryptography _cryptography;
    private readonly KnownSecretRedactor _redactor;
    private readonly HarnessSqliteOptions? _options;

    public HarnessPayloadStore(
        HarnessPayloadCryptography cryptography,
        KnownSecretRedactor redactor)
    {
        _cryptography = cryptography ?? throw new ArgumentNullException(nameof(cryptography));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public HarnessPayloadStore(
        HarnessSqliteOptions options,
        HarnessPayloadCryptography cryptography,
        KnownSecretRedactor redactor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cryptography = cryptography ?? throw new ArgumentNullException(nameof(cryptography));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public async ValueTask<ProtectedHarnessPayload> ProtectAsync(
        RunId runId,
        HarnessPayloadIntent intent,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ArgumentNullException.ThrowIfNull(intent);
        var policy = Policy(intent.Kind);
        if (intent.Plaintext.Length > policy.MaximumBytes)
        {
            throw new HarnessStoreException("payload_size_exceeded");
        }

        if (intent.Audience != policy.Audience)
        {
            throw new HarnessStoreException("payload_audience_invalid");
        }

        if (!policy.Encodings.Contains(intent.Encoding))
        {
            throw new HarnessStoreException("payload_encoding_invalid");
        }

        var protectedPlaintext = intent.Kind == HarnessPayloadKind.RecoveryOpaque
            ? intent.Plaintext.ToArray()
            : _redactor.RedactPayload(intent.Encoding, intent.Plaintext);
        if (protectedPlaintext.Length > policy.MaximumBytes)
        {
            throw new HarnessStoreException("payload_size_exceeded");
        }

        var normalized = new HarnessPayloadIntent(
            intent.PayloadId,
            intent.Kind,
            policy.Audience,
            intent.Encoding,
            policy.MaximumBytes,
            protectedPlaintext);
        var encrypted = await _cryptography.EncryptAsync(
                runId,
                normalized,
                cancellationToken)
            .ConfigureAwait(false);
        return new ProtectedHarnessPayload(normalized, encrypted);
    }

    public Task<HarnessPayloadContent> ReadForClientAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            runId,
            payloadId,
            CanReadForClient,
            "payload_client_forbidden",
            cancellationToken);

    public Task<HarnessPayloadContent> ReadForModelAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            runId,
            payloadId,
            CanReadForModel,
            "payload_model_forbidden",
            cancellationToken);

    public async Task<HarnessPayloadContent> ReadRecoveryOnceAsync(
        RunId runId,
        PayloadId payloadId,
        DateTimeOffset consumedAt,
        CancellationToken cancellationToken)
    {
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var stored = await LoadAsync(
                connection,
                transaction,
                runId,
                payloadId,
                requireActiveRun: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (stored.Intent.Kind != HarnessPayloadKind.RecoveryOpaque)
        {
            throw new HarnessStoreException("payload_recovery_forbidden");
        }

        if (stored.ConsumedAt is not null)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        var plaintext = await _cryptography.DecryptAsync(
                runId,
                stored.Intent,
                stored.Encrypted,
                cancellationToken)
            .ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_payloads
            SET consumed_at_utc = $consumed_at
            WHERE run_id = $run_id
              AND payload_id = $payload_id
              AND consumed_at_utc IS NULL
            """;
        update.Parameters.AddWithValue("$consumed_at", HarnessSqliteMapper.Utc(consumedAt));
        update.Parameters.AddWithValue("$run_id", runId.Value);
        update.Parameters.AddWithValue("$payload_id", payloadId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Content(runId, stored.Intent, plaintext);
    }

    internal ValueTask<ReadOnlyMemory<byte>> DecryptAsync(
        RunId runId,
        HarnessPayloadIntent metadata,
        EncryptedHarnessPayload encrypted,
        CancellationToken cancellationToken) =>
        _cryptography.DecryptAsync(runId, metadata, encrypted, cancellationToken);

    public static bool CanReadForClient(HarnessPayloadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Kind != HarnessPayloadKind.RecoveryOpaque
            && intent.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient);
    }

    public static bool CanReadForModel(HarnessPayloadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Kind != HarnessPayloadKind.RecoveryOpaque
            && intent.Audience.HasFlag(HarnessPayloadAudience.ModelContext);
    }

    private HarnessSqliteOptions RequireOptions() =>
        _options ?? throw new InvalidOperationException("payload_store_not_bound");

    private static HarnessPayloadContent Content(
        RunId runId,
        HarnessPayloadIntent intent,
        ReadOnlyMemory<byte> plaintext) =>
        new(
            runId,
            intent.PayloadId,
            intent.Kind,
            intent.Audience,
            intent.Encoding,
            plaintext.ToArray());

    private static PayloadPolicy Policy(HarnessPayloadKind kind) =>
        Policies.TryGetValue(kind, out var policy)
            ? policy
            : throw new HarnessStoreException("payload_kind_invalid");

    private sealed record PayloadPolicy(
        int MaximumBytes,
        HarnessPayloadAudience Audience,
        IReadOnlySet<HarnessPayloadEncoding> Encodings)
    {
        public PayloadPolicy(
            int maximumBytes,
            HarnessPayloadAudience audience,
            IEnumerable<HarnessPayloadEncoding> encodings)
            : this(maximumBytes, audience, encodings.ToHashSet())
        {
        }
    }

}

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
            [HarnessPayloadKind.ProviderContinuation] = new(
                RuAgent.Harness.Core.Prompting.ProviderContinuation.MaximumEnvelopeBytes,
                HarnessPayloadAudience.InternalOnly,
                [HarnessPayloadEncoding.Opaque]),
            [HarnessPayloadKind.AssistantStep] = new(
                2 * 1024 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                new HashSet<HarnessPayloadEncoding> { HarnessPayloadEncoding.Json },
                // Suppressed prose, an empty model reply, and the other failure paths
                // record the same payload shape for audit only, so both audiences are
                // valid for this kind.
                AlternateAudience: HarnessPayloadAudience.InternalOnly),
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
            [HarnessPayloadKind.UserImage] = new(
                HarnessPayloadIntent.MaximumByteCap,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.ModelUsage] = new(
                16 * 1024,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Json]),
            [HarnessPayloadKind.ModelContextNote] = new(
                16 * 1024,
                HarnessPayloadAudience.ModelContext,
                [HarnessPayloadEncoding.Utf8Text]),
            [HarnessPayloadKind.ModelReasoning] = new(
                32 * 1024,
                HarnessPayloadAudience.AuthenticatedClient,
                [HarnessPayloadEncoding.Utf8Text]),
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

        if (!policy.Allows(intent.Audience))
        {
            throw new HarnessStoreException("payload_audience_invalid");
        }

        if (!policy.Encodings.Contains(intent.Encoding))
        {
            throw new HarnessStoreException("payload_encoding_invalid");
        }

        var protectedPlaintext = intent.Kind is HarnessPayloadKind.RecoveryOpaque or HarnessPayloadKind.ProviderContinuation
            ? intent.Plaintext.ToArray()
            : _redactor.RedactPayload(intent.Encoding, intent.Plaintext);
        if (protectedPlaintext.Length > policy.MaximumBytes)
        {
            throw new HarnessStoreException("payload_size_exceeded");
        }

        // Carry the caller's audience rather than the policy's primary one. The policy
        // has already validated it, and the audience is what keeps suppressed prose out
        // of client reads; rewriting it here would publish exactly what a handler
        // deliberately marked audit-only.
        var normalized = new HarnessPayloadIntent(
            intent.PayloadId,
            intent.Kind,
            intent.Audience,
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

    public async Task<HarnessPayloadContent> ReadProviderContinuationAsync(
        RunId runId, PayloadId payloadId, ModelStepId modelStepId, string provider, string protocol, string model, CancellationToken cancellationToken)
    {
        var content = await ReadAsync(runId, payloadId,
            intent => intent.Kind == HarnessPayloadKind.ProviderContinuation
                && intent.Audience == HarnessPayloadAudience.InternalOnly
                && intent.Encoding == HarnessPayloadEncoding.Opaque,
            "provider_continuation_forbidden", cancellationToken).ConfigureAwait(false);
        if (content.Plaintext.Length > RuAgent.Harness.Core.Prompting.ProviderContinuation.MaximumEnvelopeBytes)
            throw new HarnessStoreException("provider_continuation_invalid");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(content.Plaintext);
            var root = document.RootElement;
            if (root.GetProperty("model_step_id").GetString() != modelStepId.Value
                || root.GetProperty("Provider").GetString() != provider
                || root.GetProperty("Protocol").GetString() != protocol
                || root.GetProperty("Model").GetString() != model
                || root.GetProperty("Version").GetInt32() != 1
                || root.GetProperty("output_items").ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new HarnessStoreException("provider_continuation_invalid");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new HarnessStoreException("provider_continuation_invalid", error);
        }
        return content;
    }

    /// <summary>
    /// Lists the payload ids of one kind for a run in insertion order. Callers
    /// that need the bytes still go through <see cref="ReadForModelAsync"/> or
    /// <see cref="ReadForClientAsync"/> so the audience rule stays enforced; this
    /// only replaces hand-written SQL in the host layer.
    /// </summary>
    public async Task<IReadOnlyList<PayloadId>> ListPayloadIdsAsync(
        RunId runId,
        HarnessPayloadKind kind,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT payload_id FROM agent_payloads WHERE run_id = $runId AND kind = $kind ORDER BY rowid ASC";
        command.Parameters.AddWithValue("$runId", runId.Value);
        command.Parameters.AddWithValue("$kind", (int)kind);
        var ids = new List<PayloadId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(new PayloadId(reader.GetString(0)));
        }

        return ids;
    }

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
        var content = await ReadRecoveryOnceAsync(
                connection,
                transaction,
                runId,
                payloadId,
                consumedAt,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return content;
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
        return intent.Kind is not (HarnessPayloadKind.RecoveryOpaque or HarnessPayloadKind.ProviderContinuation)
            && intent.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient);
    }

    public static bool CanReadForModel(HarnessPayloadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Kind is not (HarnessPayloadKind.RecoveryOpaque or HarnessPayloadKind.ProviderContinuation)
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
        IReadOnlySet<HarnessPayloadEncoding> Encodings,
        HarnessPayloadAudience AlternateAudience = HarnessPayloadAudience.None)
    {
        public PayloadPolicy(
            int maximumBytes,
            HarnessPayloadAudience audience,
            IEnumerable<HarnessPayloadEncoding> encodings)
            : this(maximumBytes, audience, encodings.ToHashSet())
        {
        }

        /// <summary>
        /// A kind may be legitimately recorded at more than one audience when the same
        /// payload shape carries both visible output and suppressed-for-audit output.
        /// An unattested single-audience policy is what let a handler pick a valid
        /// audience and still be rejected at write time.
        /// </summary>
        public bool Allows(HarnessPayloadAudience audience) =>
            audience == Audience || (AlternateAudience != HarnessPayloadAudience.None && audience == AlternateAudience);
    }

}

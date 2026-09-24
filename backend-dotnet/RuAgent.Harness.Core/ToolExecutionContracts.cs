using System.Collections.ObjectModel;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core;

public sealed record PreparedPayload
{
    public PreparedPayload(
        HarnessPayloadKind kind,
        HarnessPayloadAudience audience,
        HarnessPayloadEncoding encoding,
        int byteCap,
        ReadOnlyMemory<byte> plaintext)
    {
        var copiedPlaintext = HarnessPayloadIntent.ValidateAndCopy(
            kind,
            audience,
            encoding,
            byteCap,
            plaintext);
        Kind = kind;
        Audience = audience;
        Encoding = encoding;
        ByteCap = byteCap;
        Plaintext = copiedPlaintext;
    }

    public HarnessPayloadKind Kind { get; }

    public HarnessPayloadAudience Audience { get; }

    public HarnessPayloadEncoding Encoding { get; }

    public int ByteCap { get; }

    public ReadOnlyMemory<byte> Plaintext { get; }

    internal HarnessPayloadIntent ToIntent(PayloadId payloadId) =>
        new(payloadId, Kind, Audience, Encoding, ByteCap, Plaintext);
}

public sealed record PreparedToolExecution
{
    public PreparedToolExecution(
        ExecutionId executionId,
        ToolCallId toolCallId,
        int modelOrder,
        ToolAccessKind accessKind,
        string preparationToken,
        IReadOnlyList<PreparedPayload> preparationPayloads,
        long? gatewayGeneration = null)
    {
        _ = executionId.Value;
        _ = toolCallId.Value;
        if (modelOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modelOrder));
        }

        if (!Enum.IsDefined(accessKind))
        {
            throw new ArgumentOutOfRangeException(nameof(accessKind));
        }

        if (string.IsNullOrWhiteSpace(preparationToken) || preparationToken.Length > 4096)
        {
            throw new ArgumentException(
                "Preparation token must be bounded and non-empty.",
                nameof(preparationToken));
        }

        if (gatewayGeneration is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayGeneration));
        }

        ArgumentNullException.ThrowIfNull(preparationPayloads);
        var payloads = preparationPayloads.ToArray();
        if (payloads.Any(payload => payload is null))
        {
            throw new ArgumentException(
                "Preparation payloads cannot contain null.",
                nameof(preparationPayloads));
        }

        ExecutionId = executionId;
        ToolCallId = toolCallId;
        ModelOrder = modelOrder;
        AccessKind = accessKind;
        PreparationToken = preparationToken;
        PreparationPayloads = new ReadOnlyCollection<PreparedPayload>(payloads);
        GatewayGeneration = gatewayGeneration;
    }

    public ExecutionId ExecutionId { get; }

    public ToolCallId ToolCallId { get; }

    public int ModelOrder { get; }

    public ToolAccessKind AccessKind { get; }

    public string PreparationToken { get; }

    public IReadOnlyList<PreparedPayload> PreparationPayloads { get; }

    public long? GatewayGeneration { get; }
}

public enum ToolExecutionOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    OutcomeUnknown,
}

public enum ToolOutputStream
{
    Stdout,
    Stderr,
    Progress,
}

public sealed record ToolOutputChunk
{
    public ToolOutputChunk(
        ExecutionId executionId,
        long sequence,
        ToolOutputStream stream,
        string content)
    {
        _ = executionId.Value;
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        if (string.IsNullOrEmpty(content) || content.Length > 64 * 1024)
        {
            throw new ArgumentException("Tool output chunk must be bounded.", nameof(content));
        }

        ExecutionId = executionId;
        Sequence = sequence;
        Stream = stream;
        Content = content;
    }

    public ExecutionId ExecutionId { get; }

    public long Sequence { get; }

    public ToolOutputStream Stream { get; }

    public string Content { get; }
}

public sealed class ToolTransportException : Exception
{
    public ToolTransportException(string message)
        : base(message)
    {
    }
}

public sealed record ToolExecutionReceipt
{
    public ToolExecutionReceipt(
        ExecutionId executionId,
        ToolCallId toolCallId,
        ToolExecutionOutcome outcome,
        string code,
        PayloadId? resultPayloadId,
        bool sideEffectsPossible,
        ReadOnlyMemory<byte> modelContent = default,
        HarnessPayloadEncoding contentEncoding = HarnessPayloadEncoding.Json,
        string? providerCallId = null)
    {
        _ = executionId.Value;
        _ = toolCallId.Value;
        if (resultPayloadId is { } payloadId)
        {
            _ = payloadId.Value;
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!IsSafeCode(code))
        {
            throw new ArgumentException("Receipt code must be a bounded safe token.", nameof(code));
        }

        if (!Enum.IsDefined(contentEncoding))
        {
            throw new ArgumentOutOfRangeException(nameof(contentEncoding));
        }

        if (providerCallId is not null && !ProviderCallIdContract.IsValid(providerCallId))
        {
            throw new ArgumentException("Provider call identifier is invalid.", nameof(providerCallId));
        }

        if (modelContent.Length > HarnessPayloadIntent.MaximumByteCap)
        {
            throw new ArgumentException("Tool result content exceeds the Core cap.", nameof(modelContent));
        }

        ExecutionId = executionId;
        ToolCallId = toolCallId;
        Outcome = outcome;
        Code = code;
        ResultPayloadId = resultPayloadId;
        SideEffectsPossible = sideEffectsPossible;
        ModelContent = modelContent.ToArray();
        ContentEncoding = contentEncoding;
        ProviderCallId = providerCallId;
    }

    public ExecutionId ExecutionId { get; }

    public ToolCallId ToolCallId { get; }

    public ToolExecutionOutcome Outcome { get; }

    public string Code { get; }

    public PayloadId? ResultPayloadId { get; }

    public bool SideEffectsPossible { get; }

    public ReadOnlyMemory<byte> ModelContent { get; }

    public HarnessPayloadEncoding ContentEncoding { get; }

    public string? ProviderCallId { get; }

    internal ToolExecutionReceipt WithProviderCallId(string providerCallId) =>
        new(
            ExecutionId,
            ToolCallId,
            Outcome,
            Code,
            ResultPayloadId,
            SideEffectsPossible,
            ModelContent,
            ContentEncoding,
            ProviderCallId is null
                ? providerCallId
                : string.Equals(ProviderCallId, providerCallId, StringComparison.Ordinal)
                    ? ProviderCallId
                    : throw new ArgumentException("Provider call identifier mismatch.", nameof(providerCallId)));

    /// <summary>
    /// Returns a copy of this receipt that names the payload its content was stored under.
    /// </summary>
    /// <remarks>
    /// The model content is carried over rather than dropped. This used to pass
    /// <c>default</c>, which emptied it: the payload intent had already been handed to the
    /// store, so the bytes were considered spent, but readers downstream still consult
    /// <see cref="ModelContent"/> to build an edit preview and to raise derived activity.
    /// Dropping it made those readers see an empty result for a call that succeeded, and
    /// the failure was silent because an empty result is a legal thing for a tool to
    /// return.
    /// </remarks>
    internal ToolExecutionReceipt WithResultPayload(PayloadId payloadId) =>
        new(
            ExecutionId,
            ToolCallId,
            Outcome,
            Code,
            payloadId,
            SideEffectsPossible,
            ModelContent,
            ContentEncoding,
            ProviderCallId);

    private static bool IsSafeCode(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
}

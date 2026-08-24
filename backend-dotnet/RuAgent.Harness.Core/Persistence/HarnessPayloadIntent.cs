namespace RuAgent.Harness.Core.Persistence;

public enum HarnessPayloadKind
{
    UserPrompt,
    AssistantText,
    ToolArguments,
    ToolOutput,
    Diff,
    ContinuationSummary,
    RecoveryOpaque,
}

[Flags]
public enum HarnessPayloadAudience
{
    None = 0,
    AuthenticatedClient = 1,
    ModelContext = 2,
    InternalOnly = 4,
}

public enum HarnessPayloadEncoding
{
    Utf8Text,
    Json,
    Opaque,
}

public sealed record HarnessPayloadIntent
{
    public const int MaximumByteCap = 16 * 1024 * 1024;

    public HarnessPayloadIntent(
        PayloadId payloadId,
        HarnessPayloadKind kind,
        HarnessPayloadAudience audience,
        HarnessPayloadEncoding encoding,
        int byteCap,
        ReadOnlyMemory<byte> plaintext)
    {
        _ = payloadId.Value;
        var copiedPlaintext = ValidateAndCopy(
            kind,
            audience,
            encoding,
            byteCap,
            plaintext);
        PayloadId = payloadId;
        Kind = kind;
        Audience = audience;
        Encoding = encoding;
        ByteCap = byteCap;
        Plaintext = copiedPlaintext;
    }

    public PayloadId PayloadId { get; }

    public HarnessPayloadKind Kind { get; }

    public HarnessPayloadAudience Audience { get; }

    public HarnessPayloadEncoding Encoding { get; }

    public int ByteCap { get; }

    public ReadOnlyMemory<byte> Plaintext { get; }

    internal static byte[] ValidateAndCopy(
        HarnessPayloadKind kind,
        HarnessPayloadAudience audience,
        HarnessPayloadEncoding encoding,
        int byteCap,
        ReadOnlyMemory<byte> plaintext)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(encoding))
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        const HarnessPayloadAudience allAudiences =
            HarnessPayloadAudience.AuthenticatedClient
            | HarnessPayloadAudience.ModelContext
            | HarnessPayloadAudience.InternalOnly;
        if (audience == HarnessPayloadAudience.None || (audience & ~allAudiences) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audience));
        }

        if (audience.HasFlag(HarnessPayloadAudience.InternalOnly)
            && audience != HarnessPayloadAudience.InternalOnly)
        {
            throw new ArgumentException(
                "InternalOnly cannot be combined with another audience.",
                nameof(audience));
        }

        if (byteCap is <= 0 or > MaximumByteCap)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCap));
        }

        if (plaintext.Length > byteCap)
        {
            throw new ArgumentException("Payload exceeds its declared byte cap.", nameof(plaintext));
        }

        if (kind == HarnessPayloadKind.RecoveryOpaque
            && (audience != HarnessPayloadAudience.InternalOnly
                || encoding != HarnessPayloadEncoding.Opaque))
        {
            throw new ArgumentException(
                "RecoveryOpaque must be opaque and internal-only.",
                nameof(kind));
        }

        return plaintext.ToArray();
    }
}

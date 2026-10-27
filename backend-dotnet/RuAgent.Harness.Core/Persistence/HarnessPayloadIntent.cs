namespace RuAgent.Harness.Core.Persistence;

public enum HarnessPayloadKind
{
    UserPrompt = 0,
    AssistantText = 1,
    ToolArguments = 2,
    ToolOutput = 3,
    Diff = 4,
    ContinuationSummary = 5,
    RecoveryOpaque = 6,
    AssistantStep = 7,
    ModelContextBase = 8,
    ModelContextSummary = 9,
    PromptReceipt = 10,
    UserImage = 11,
    ModelUsage = 12,
    ModelContextNote = 13,
    ModelReasoning = 14,
    ProviderContinuation = 15,
    UserInputRequest = 16,
    RuntimeContextSnapshot = 17,
    ToolImage = 18,
    SkillInvocation = 19,
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
    Utf8Text = 0,
    Json = 1,
    Opaque = 2,
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

        if (kind == HarnessPayloadKind.ProviderContinuation
            && (audience != HarnessPayloadAudience.InternalOnly || encoding != HarnessPayloadEncoding.Opaque))
        {
            throw new ArgumentException("ProviderContinuation must be opaque and internal-only.", nameof(kind));
        }

        if (kind == HarnessPayloadKind.ModelContextSummary
            && (audience != HarnessPayloadAudience.ModelContext
                || encoding != HarnessPayloadEncoding.Utf8Text))
        {
            throw new ArgumentException(
                "ModelContextSummary must be UTF-8 and model-only.",
                nameof(kind));
        }

        if (kind is HarnessPayloadKind.ModelContextNote or HarnessPayloadKind.RuntimeContextSnapshot or HarnessPayloadKind.SkillInvocation
            && (audience != HarnessPayloadAudience.ModelContext
                || encoding != HarnessPayloadEncoding.Utf8Text))
        {
            throw new ArgumentException(
                "ModelContextNote must be UTF-8 and model-only.",
                nameof(kind));
        }

        if (kind == HarnessPayloadKind.UserImage
            && (audience != (HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext)
                || encoding != HarnessPayloadEncoding.Utf8Text))
        {
            throw new ArgumentException(
                "UserImage must be UTF-8 and visible to client and model.",
                nameof(kind));
        }

        if (kind == HarnessPayloadKind.ToolImage
            && (audience != HarnessPayloadAudience.ModelContext
                || encoding != HarnessPayloadEncoding.Opaque))
        {
            throw new ArgumentException(
                "ToolImage must be opaque and model-only.",
                nameof(kind));
        }

        return plaintext.ToArray();
    }
}

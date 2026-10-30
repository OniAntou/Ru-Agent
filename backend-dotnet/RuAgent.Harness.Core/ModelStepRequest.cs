using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

public sealed record ModelStepRequest
{
    public ModelStepRequest(
        RunId runId,
        ModelStepId modelStepId,
        ModelContextSnapshot context,
        AgentPermissionPreset preset,
        long policySnapshotVersion,
        string policySnapshotSha256,
        SessionId? sessionId = null)
        : this(
            runId,
            modelStepId,
            context,
            SystemPromptContext.Empty,
            preset,
            policySnapshotVersion,
            policySnapshotSha256,
            null,
            sessionId)
    {
    }

    public ModelStepRequest(
        RunId runId,
        ModelStepId modelStepId,
        ModelContextSnapshot context,
        SystemPromptContext promptContext,
        AgentPermissionPreset preset,
        long policySnapshotVersion,
        string policySnapshotSha256,
        IPreparedModelStep? preparation = null,
        SessionId? sessionId = null)
    {
        _ = runId.Value;
        _ = modelStepId.Value;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(promptContext);
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (policySnapshotVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policySnapshotVersion));
        }

        if (policySnapshotSha256 is not { Length: 64 }
            || policySnapshotSha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Invalid policy snapshot hash.", nameof(policySnapshotSha256));
        }

        RunId = runId;
        ModelStepId = modelStepId;
        Context = context;
        PromptContext = promptContext;
        Preset = preset;
        PolicySnapshotVersion = policySnapshotVersion;
        PolicySnapshotSha256 = policySnapshotSha256;
        Preparation = preparation;
        SessionId = sessionId;
    }

    public RunId RunId { get; }
    public ModelStepId ModelStepId { get; }
    public ModelContextSnapshot Context { get; }
    public SystemPromptContext PromptContext { get; }
    public AgentPermissionPreset Preset { get; }
    public long PolicySnapshotVersion { get; }
    public string PolicySnapshotSha256 { get; }
    public IPreparedModelStep? Preparation { get; }

    /// <summary>
    /// The conversation this step belongs to, when the caller knows it. A provider
    /// that routes by conversation (OpenCode Go) needs a stable value here; a step
    /// without one fails closed rather than sending an unroutable request.
    /// </summary>
    public SessionId? SessionId { get; }

    public ModelStepRequest WithPreparation(IPreparedModelStep preparation) => new(
        RunId, ModelStepId, Context, PromptContext, Preset, PolicySnapshotVersion,
        PolicySnapshotSha256, preparation ?? throw new ArgumentNullException(nameof(preparation)),
        SessionId);
}

public sealed record AssistantDelta
{
    public AssistantDelta(ModelStepId modelStepId, long sequence, string content, bool isReset = false)
    {
        _ = modelStepId.Value;
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (isReset)
        {
            // A reset frame carries no text: it tells the client to drop whatever
            // partial answer a superseded attempt had already streamed.
            if (!string.IsNullOrEmpty(content))
            {
                throw new ArgumentException("A reset delta must not carry content.", nameof(content));
            }
        }
        else if (string.IsNullOrEmpty(content) || content.Length > 64 * 1024)
        {
            throw new ArgumentException("Assistant delta must be bounded and non-empty.", nameof(content));
        }

        ModelStepId = modelStepId;
        Sequence = sequence;
        Content = content;
        IsReset = isReset;
    }

    public ModelStepId ModelStepId { get; }
    public long Sequence { get; }
    public string Content { get; }

    /// <summary>
    /// True when this frame supersedes the text streamed so far for the step. The
    /// step runner retries a transient failure on a fresh socket, so the client
    /// must discard the failed attempt's partial answer instead of appending the
    /// retry onto it.
    /// </summary>
    public bool IsReset { get; }
}

using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core;

public sealed record ModelStepRequest
{
    public ModelStepRequest(
        RunId runId,
        ModelStepId modelStepId,
        ModelContextSnapshot context,
        AgentPermissionPreset preset,
        long policySnapshotVersion,
        string policySnapshotSha256)
    {
        _ = runId.Value;
        _ = modelStepId.Value;
        ArgumentNullException.ThrowIfNull(context);
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
        Preset = preset;
        PolicySnapshotVersion = policySnapshotVersion;
        PolicySnapshotSha256 = policySnapshotSha256;
    }

    public RunId RunId { get; }
    public ModelStepId ModelStepId { get; }
    public ModelContextSnapshot Context { get; }
    public AgentPermissionPreset Preset { get; }
    public long PolicySnapshotVersion { get; }
    public string PolicySnapshotSha256 { get; }
}

public sealed record AssistantDelta
{
    public AssistantDelta(ModelStepId modelStepId, long sequence, string content)
    {
        _ = modelStepId.Value;
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (string.IsNullOrEmpty(content) || content.Length > 64 * 1024)
        {
            throw new ArgumentException("Assistant delta must be bounded and non-empty.", nameof(content));
        }

        ModelStepId = modelStepId;
        Sequence = sequence;
        Content = content;
    }

    public ModelStepId ModelStepId { get; }
    public long Sequence { get; }
    public string Content { get; }
}

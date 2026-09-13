using RuAgent.Harness.Core;
using RuBackend.Conversation;

namespace RuBackend.Harness;

public sealed class AgentReplyPolicy : IAgentReplyPolicy
{
    private readonly IMetadataPolicy _metadata;

    public AgentReplyPolicy(IMetadataPolicy metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public ValueTask<AgentReplyPresentation> PrepareAsync(
        AgentRun run,
        string rawText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rawText);
        cancellationToken.ThrowIfCancellationRequested();

        var (splitVisible, emotion) = _metadata.SplitReply(rawText);
        var visible = _metadata.StripVoiceMarkers(splitVisible);
        return ValueTask.FromResult(new AgentReplyPresentation(visible, emotion: emotion));
    }
}

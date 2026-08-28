using RuAgent.Harness.Core;
using RuBackend.Conversation;
using RuBackend.Worker;

namespace RuBackend.Harness;

public interface IAgentVtsClient
{
    Task<VtsCatalog?> GetCatalogAsync(
        string requestId,
        CancellationToken cancellationToken);

    Task TriggerAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken);
}

public sealed class WorkerAgentVtsClient : IAgentVtsClient
{
    private readonly IVtsProtocolClient _worker;

    public WorkerAgentVtsClient(IVtsProtocolClient worker)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
    }

    public Task<VtsCatalog?> GetCatalogAsync(
        string requestId,
        CancellationToken cancellationToken) =>
        _worker.GetVtsCatalogAsync(requestId, cancellationToken);

    public Task TriggerAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken) =>
        _worker.TriggerVtsActionsAsync(requestId, codes, cancellationToken);
}

public sealed class AgentReplyPolicy : IAgentReplyPolicy
{
    private readonly IMetadataPolicy _metadata;
    private readonly IAgentVtsClient _vts;

    public AgentReplyPolicy(IMetadataPolicy metadata, IAgentVtsClient vts)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _vts = vts ?? throw new ArgumentNullException(nameof(vts));
    }

    public async ValueTask<AgentReplyPresentation> PrepareAsync(
        AgentRun run,
        string rawText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rawText);

        VtsCatalog? catalog;
        try
        {
            catalog = await _vts.GetCatalogAsync(
                    $"agent-{run.RunId.Value}-catalog",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is WorkerProtocolException or WorkerUnavailableException)
        {
            catalog = null;
        }

        var (splitVisible, codes, emotion) = _metadata.SplitReply(rawText, catalog);
        var visible = _metadata.StripVoiceMarkers(splitVisible);
        return new AgentReplyPresentation(visible, codes, emotion);
    }
}

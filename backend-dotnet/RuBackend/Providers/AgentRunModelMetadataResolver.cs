using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Providers;

public sealed record AgentRunModelMetadata(
    string ModelLabel,
    string Protocol,
    ProviderToolCallingMode ToolCallingMode,
    long? ContextWindowTokens,
    string ContextWindowSource,
    string ContextWindowVersion,
    int OutputTokenReserve,
    string ToolProfile = RuAgent.Protocol.AgentToolProfiles.Minimal,
    bool ComputerUse = false,
    bool ProviderWebSearch = false);

public interface IAgentRunModelMetadataResolver
{
    Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken);
}

public sealed class AgentRunModelMetadataResolver(
    AgentProviderSnapshotFactory snapshots,
    SqliteAgentRunProviderSelectionStore selections) : IAgentRunModelMetadataResolver
{
    public async Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await snapshots.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            var state = await selections.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            var selection = state?.Selection
                ?? throw new ProviderCatalogException("provider_selection_missing");
            var modelLabel = selection.ModelLabel
                ?? throw new ProviderCatalogException("provider_selection_invalid");
            if (!AgentRunCallingModeCodec.TryParseStoredValue(selection.CallingMode, out var callingMode))
            {
                throw new ProviderCatalogException("provider_selection_invalid");
            }
            return new AgentRunModelMetadata(
                modelLabel,
                selection.Protocol,
                callingMode,
                selection.ContextWindowTokens,
                selection.ContextWindowSource,
                selection.ContextWindowVersion,
                selection.OutputTokenReserve,
                RuAgent.Protocol.AgentToolProfiles.ReadPin(System.Text.Json.Nodes.JsonNode.Parse(selection.RequestOptionsJson)!.AsObject()),
                ComputerUsePolicy.IsPinned(selection),
                ProviderWebSearchPolicy.IsPinned(selection));
        }
        catch (HarnessStoreException error)
        {
            throw new ProviderCatalogException(error.Code);
        }
    }
}

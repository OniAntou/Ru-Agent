using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Providers;

namespace RuBackend.Harness;

/// <summary>What a new run's base capture needs to know about its pinned model.</summary>
public sealed record RunHandoffModelFacts(long? KnownContextWindowTokens);

public interface IRunHandoffModelFacts
{
    /// <summary>
    /// Resolves (and pins, when it is not pinned yet) the run's model. Null when it cannot be
    /// resolved now: the capture then keeps the default handoff and no native continuation.
    /// </summary>
    Task<RunHandoffModelFacts?> ReadAsync(RunId runId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether two runs share the provider endpoint, model, protocol and every pin that
    /// changes the declared tool set, so one can read the other's history natively.
    /// </summary>
    Task<bool> SharesModelAsync(RunId runId, RunId sourceRunId, CancellationToken cancellationToken);
}

public sealed class AgentRunHandoffModelFacts(
    IAgentRunModelMetadataResolver metadata,
    SqliteAgentRunProviderSelectionStore selections) : IRunHandoffModelFacts
{
    // Windows a catalog or the user published. "assumed" and "unknown" are guesses and keep
    // the default handoff size.
    private static readonly HashSet<string> KnownWindowSources = new(StringComparer.Ordinal)
    {
        "provider_catalog",
        "models.dev",
        "user_setting",
    };

    /// <summary>
    /// How long capture waits for the selection. Capture runs on the conversation outbox, so
    /// a slow catalog must not hold message delivery; past this the run keeps the defaults.
    /// </summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    public async Task<RunHandoffModelFacts?> ReadAsync(RunId runId, CancellationToken cancellationToken)
    {
        AgentRunModelMetadata resolved;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ResolveTimeout);
        try
        {
            resolved = await metadata.ResolveAsync(runId, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The capture must not fail on a provider it does not need: the run's first step
            // resolves the same selection and reports its own named failure.
            return null;
        }
        return new RunHandoffModelFacts(KnownWindowSources.Contains(resolved.ContextWindowSource)
            ? resolved.ContextWindowTokens
            : null);
    }

    public async Task<bool> SharesModelAsync(RunId runId, RunId sourceRunId, CancellationToken cancellationToken)
    {
        var current = (await selections.GetAsync(runId, cancellationToken).ConfigureAwait(false))?.Selection;
        var source = (await selections.GetAsync(sourceRunId, cancellationToken).ConfigureAwait(false))?.Selection;
        if (current is null || source is null) return false;
        return string.Equals(current.Backend, source.Backend, StringComparison.Ordinal)
            && string.Equals(current.Alias, source.Alias, StringComparison.Ordinal)
            && string.Equals(current.Protocol, source.Protocol, StringComparison.Ordinal)
            && string.Equals(current.BaseUri, source.BaseUri, StringComparison.Ordinal)
            && string.Equals(current.Model, source.Model, StringComparison.Ordinal)
            && string.Equals(current.Account, source.Account, StringComparison.Ordinal)
            && string.Equals(current.CallingMode, source.CallingMode, StringComparison.Ordinal)
            && string.Equals(ToolProfile(current), ToolProfile(source), StringComparison.Ordinal)
            && ComputerUsePolicy.IsPinned(current) == ComputerUsePolicy.IsPinned(source)
            && BrowserPolicy.IsPinned(current) == BrowserPolicy.IsPinned(source)
            && BrowserPolicy.IsPersistentProfilePinned(current) == BrowserPolicy.IsPersistentProfilePinned(source)
            && ProviderWebSearchPolicy.IsPinned(current) == ProviderWebSearchPolicy.IsPinned(source);
    }

    // An older pin may carry no profile; two such pins compare equal, as they declare the same tools.
    private static string? ToolProfile(AgentRunProviderSelection selection)
    {
        if (JsonNode.Parse(selection.RequestOptionsJson) is not JsonObject options) return null;
        try
        {
            return RuAgent.Protocol.AgentToolProfiles.ReadPin(options);
        }
        catch (RuAgent.Protocol.AgentProtocolException)
        {
            return null;
        }
    }
}

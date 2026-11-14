using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Harness.Subagents;

namespace RuBackend.Providers;

/// <summary>A named reason the advisor cannot be consulted (ADR 0047).</summary>
public sealed class AdvisorResolutionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>The provider snapshot one consultation uses, and the model it names.</summary>
public sealed record AdvisorTarget(ProviderBootstrapData Bootstrap, string Model);

/// <summary>
/// Resolves which model a consultation asks (ADR 0047). Without arguments it is the run's
/// pinned advisor with its saved effort. A model or effort the main model passes is accepted
/// only when the run's own authenticated user prompt names it, by the rule ADR 0022 uses for a
/// child model, and a requested model resolves only to an exact catalog id of a signed-in
/// provider. Nothing is substituted.
/// </summary>
public sealed class AdvisorTargetResolver(
    SqliteAgentRunProviderSelectionStore selections,
    AgentProviderSnapshotFactory snapshots,
    ISubagentModelSelectionProvider requestedModels,
    HarnessPayloadStore payloads)
{
    public async Task<AdvisorTarget> ResolveAsync(RunId runId, string? model, string? effort, CancellationToken ct)
    {
        var selection = (await selections.GetAsync(runId, ct).ConfigureAwait(false))?.Selection
            ?? throw new AdvisorResolutionException(AdvisorCodes.NotSelected);
        var pin = AdvisorPolicy.ReadPinned(selection) ?? throw new AdvisorResolutionException(AdvisorCodes.NotSelected);
        if (effort is not null
            && !SubagentSelectionResolver.MentionsModel(await ReadUserPromptAsync(runId, ct).ConfigureAwait(false), effort))
            throw new AdvisorResolutionException(AdvisorCodes.EffortUserRequestRequired);

        AgentRunProviderSelection target;
        if (model is not null)
        {
            try
            {
                // The same evidence rule and catalog match a user-requested child model uses.
                target = await requestedModels.ResolveAsync(runId, SubagentRole.Review, ct, model).ConfigureAwait(false);
            }
            catch (SubagentConfigurationException error)
            {
                throw new AdvisorResolutionException(MapRequestedModelCode(error.Code));
            }
        }
        else
        {
            target = pin.Selection
                ?? throw new AdvisorResolutionException(pin.UnavailableCode ?? AdvisorCodes.NotSelected);
        }

        try
        {
            var bootstrap = await snapshots.BuildAdvisorSnapshotAsync(target, effort, ct).ConfigureAwait(false);
            return new AdvisorTarget(bootstrap, target.Model);
        }
        catch (ProviderCatalogException)
        {
            throw new AdvisorResolutionException(AdvisorCodes.Unavailable);
        }
    }

    private async Task<string> ReadUserPromptAsync(RunId runId, CancellationToken ct)
    {
        var ids = await payloads.ListPayloadIdsAsync(runId, HarnessPayloadKind.UserPrompt, ct).ConfigureAwait(false);
        if (ids.Count == 0) return string.Empty;
        var content = await payloads.ReadForModelAsync(runId, ids[0], ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(content.Plaintext.Span);
    }

    internal static string MapRequestedModelCode(string code) => code switch
    {
        "subagent_model_user_request_required" => AdvisorCodes.ModelUserRequestRequired,
        "subagent_model_provider_ambiguous" => AdvisorCodes.ModelAmbiguous,
        "subagent_model_catalog_unavailable" => AdvisorCodes.CatalogUnavailable,
        "subagent_parent_model_missing" => AdvisorCodes.NotSelected,
        _ => AdvisorCodes.ModelUnavailable,
    };
}

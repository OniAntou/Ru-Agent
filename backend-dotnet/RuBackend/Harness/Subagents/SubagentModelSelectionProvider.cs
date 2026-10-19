using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;
using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Harness.Subagents;

public interface ISubagentModelSelectionProvider
{
    Task<AgentRunProviderSelection> ResolveAsync(RunId rootRunId, SubagentRole role, CancellationToken ct,
        string? model = null, string? providerId = null);
}

public sealed class SubagentModelSelectionProvider(SqliteAgentRunProviderSelectionStore selections,
    ISettingsStore settings, AgentProviderSnapshotFactory snapshots, HarnessPayloadStore payloads,
    ProviderCatalogService catalog, ICredentialStore credentials) : ISubagentModelSelectionProvider
{
    public async Task<AgentRunProviderSelection> ResolveAsync(RunId rootRunId, SubagentRole role, CancellationToken ct,
        string? model = null, string? providerId = null)
    {
        var parent = (await selections.GetAsync(rootRunId, ct).ConfigureAwait(false))?.Selection
            ?? throw new SubagentConfigurationException("subagent_parent_model_missing");
        // Only the original authenticated prompt can opt in; never workspace or tool output.
        var prompt = "";
        if (model is not null || providerId is not null)
        {
            var ids = await payloads.ListPayloadIdsAsync(rootRunId, HarnessPayloadKind.UserPrompt, ct).ConfigureAwait(false);
            if (ids.Count > 0)
            {
                var content = await payloads.ReadForModelAsync(rootRunId, ids[0], ct).ConfigureAwait(false);
                prompt = Encoding.UTF8.GetString(content.Plaintext.Span);
            }
        }
        return await SubagentSelectionResolver.ResolveAsync(parent, role, model, providerId, prompt,
            (name, provider, token) => ResolveRequestedAsync(parent, name, provider, token), ct).ConfigureAwait(false);
    }

    private async Task<AgentRunProviderSelection> ResolveRequestedAsync(AgentRunProviderSelection parent,
        string requested, string? providerId, CancellationToken ct)
    {
        var current = await settings.ReadAsync(ct).ConfigureAwait(false);
        var providers = new List<string> { "openai", "anthropic", "custom" };
        if (providerId is null || ProviderSelection.NormalizeModelsDevBackend(providerId) is not null)
            providers.AddRange((await catalog.GetModelsDevAsync(ct).ConfigureAwait(false))
                .Select(p => ProviderSelection.NormalizeModelsDevBackend("models.dev:" + p.Id)).OfType<string>());
        var matches = new List<SettingsSnapshot>();
        var catalogUnavailable = false;
        foreach (var provider in providers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (providerId is not null && !string.Equals(provider, providerId, StringComparison.OrdinalIgnoreCase)) continue;
            var account = ProviderSelection.CredentialAccountForAlias(provider);
            // Overrides require sign-in even for custom endpoints that ordinarily support keyless use.
            if (account is null || string.IsNullOrWhiteSpace(await credentials.GetAsync("Ru", account, ct).ConfigureAwait(false))) continue;
            var backend = provider switch { "openai" => "OpenAI", "anthropic" => "Anthropic", "custom" => "Custom OpenAI", _ => provider };
            try
            {
                var candidate = current with { Backend = backend };
                var available = await catalog.GetAsync(candidate, false, ct).ConfigureAwait(false);
                if (available.ErrorCode is not null || available.Stale) { catalogUnavailable = true; continue; }
                foreach (var item in available.Catalog.Where(m => m.Supported && SubagentSelectionResolver.MatchesModel(m.Id, requested)))
                    matches.Add(candidate with { Model = item.Id, CustomModel = item.Id, OpenAiModel = item.Id, AnthropicModel = item.Id });
            }
            catch (Exception e) when (e is ProviderCatalogException or ArgumentException or HttpRequestException)
            { catalogUnavailable = true; }
        }
        var preferred = matches.Where(m => m.Backend == parent.Backend).ToArray();
        var choices = preferred.Length == 1 ? preferred : matches.ToArray();
        if (choices.Length == 0) throw new SubagentConfigurationException(catalogUnavailable
            ? "subagent_model_catalog_unavailable" : "subagent_model_unavailable_signed_in");
        if (choices.Length != 1) throw new SubagentConfigurationException("subagent_model_provider_ambiguous");
        try { return await snapshots.DescribeSelectionAsync(choices[0], ct, requireCatalogModel: true).ConfigureAwait(false); }
        catch (ProviderCatalogException) { throw new SubagentConfigurationException("subagent_model_catalog_unavailable"); }
    }
}

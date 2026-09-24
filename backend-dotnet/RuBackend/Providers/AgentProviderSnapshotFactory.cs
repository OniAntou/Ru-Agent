using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Pins the provider choice for a run, writes a single-use DPAPI handoff per step.</summary>
public sealed class AgentProviderSnapshotFactory(
    ISettingsStore settings, ICredentialStore credentials, ProviderCatalogService catalog,
    string bootstrapRoot, Func<RunId, CancellationToken, Task<bool>> isTerminal) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RunId, ProviderBootstrapData> _runs = [];
    private bool _disposed;

    public async Task<string> CreateAsync(ModelStepRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_runs.TryGetValue(request.RunId, out var snapshot))
            {
                foreach (var id in _runs.Keys.ToArray())
                    if (await isTerminal(id, ct).ConfigureAwait(false)) _runs.Remove(id);
                if (_runs.Count >= 64) throw new ProviderCatalogException("provider_snapshot_capacity");
                var current = await settings.ReadAsync(ct).ConfigureAwait(false);
                var selected = await catalog.ResolveSelectionAsync(current, ct).ConfigureAwait(false);
                var key = selected.Account is null ? "" : await credentials.GetAsync("Ru", selected.Account, ct).ConfigureAwait(false) ?? "";
                // Fail fast before any network fetch: without its credential or model
                // a run can never produce a reply, so report it now instead of
                // after connect/receive timeouts and retry backoffs.
                if (selected.RequiresCredential && string.IsNullOrWhiteSpace(key))
                    throw new ProviderCatalogException("catalog_unauthorized");
                if ((selected.Alias is "openai" or "anthropic") && string.IsNullOrWhiteSpace(selected.Model))
                    throw new ProviderCatalogException("model_unavailable");
                var models = await catalog.GetAsync(current, false, ct).ConfigureAwait(false);
                var options = ProviderReasoningPreferences.Resolve(models, selected.Model, ProviderReasoningPreferences.Read(current, selected));
                var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                if (revision != models.CredentialRevision) throw new ProviderCatalogException("provider_configuration_changed");
                if (selected.Alias is "openai" or "anthropic")
                {
                    if (!ProviderCapabilityRegistry.TryGet(selected.Alias, selected.Model, out var capability))
                        throw new ProviderCatalogException("model_unavailable");
                    var maximumOutput = selected.Protocol == ProviderProtocols.AnthropicMessages
                        ? Math.Min(capability.DocumentedMaxOutputTokens!.Value, 8192)
                        : (int?)null;
                    snapshot = new(selected.Backend, selected.Protocol, selected.CompletionUri, selected.Model, key, options, maximumOutput);
                }
                else
                {
                    snapshot = new(selected.Backend, selected.Protocol, selected.CompletionUri, selected.Model, key, options, null);
                }
                _runs.Add(request.RunId, snapshot);
            }
            ct.ThrowIfCancellationRequested();
            return ProtectedProviderBootstrap.Write(bootstrapRoot, snapshot);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _runs.Clear(); }
        finally { _gate.Release(); }
    }
}

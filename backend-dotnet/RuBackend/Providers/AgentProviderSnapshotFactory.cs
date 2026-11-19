using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>Pins the provider choice for a run and hands the snapshot to the in-process step runner.</summary>
public sealed class AgentProviderSnapshotFactory(
    ISettingsStore settings,
    ICredentialStore credentials,
    ProviderCatalogService catalog,
    SqliteAgentRunProviderSelectionStore selectionStore,
    Func<RunId, CancellationToken, Task<bool>> isTerminal,
    RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? secrets = null) : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan SynchronousDisposeTimeout = TimeSpan.FromSeconds(5);
    private readonly object _cacheGate = new();
    private readonly object _disposeSync = new();
    private readonly Dictionary<RunId, ProviderBootstrapData> _runs = [];
    private readonly Dictionary<RunId, Task<ProviderBootstrapData>> _inflight = [];
    private Task? _disposeTask;
    private int _disposeRequested;
    private bool _disposed;

    public async Task<ProviderBootstrapData> GetAsync(RunId runId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        while (true)
        {
            Task<ProviderBootstrapData>? flight = null;
            TaskCompletionSource<ProviderBootstrapData>? owner = null;
            while (flight is null)
            {
                var shouldEvict = false;
                lock (_cacheGate)
                {
                    ObjectDisposedException.ThrowIf(_disposeRequested != 0 || _disposed, this);
                    if (_runs.TryGetValue(runId, out var cached)) return cached;
                    if (_inflight.TryGetValue(runId, out flight)) break;
                    if (_runs.Count + _inflight.Count < 64)
                    {
                        owner = new TaskCompletionSource<ProviderBootstrapData>(TaskCreationOptions.RunContinuationsAsynchronously);
                        flight = owner.Task;
                        _inflight.Add(runId, flight);
                    }
                    else
                    {
                        shouldEvict = true;
                    }
                }

                if (shouldEvict)
                {
                    await RemoveTerminalSnapshotsAsync(ct).ConfigureAwait(false);
                    lock (_cacheGate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeRequested != 0 || _disposed, this);
                        if (!_runs.ContainsKey(runId) && !_inflight.ContainsKey(runId)
                            && _runs.Count + _inflight.Count >= 64)
                            throw new ProviderCatalogException("provider_snapshot_capacity");
                    }
                }
            }

            if (owner is not null) _ = LoadAndPublishAsync(runId, owner, ct);
            try
            {
                return await flight!.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && flight!.IsCanceled)
            {
                // The caller that owned the shared load canceled it. A waiter with its
                // own live token retries after that flight has been removed.
            }
        }
    }

    private async Task LoadAndPublishAsync(RunId runId,
        TaskCompletionSource<ProviderBootstrapData> completion, CancellationToken ct)
    {
        ProviderBootstrapData? snapshot = null;
        Exception? failure = null;
        try
        {
            await RemoveTerminalSnapshotsAsync(ct).ConfigureAwait(false);
            var state = await selectionStore.GetAsync(runId, ct).ConfigureAwait(false);
            if (state is null) throw new ProviderCatalogException("provider_selection_missing");
            var current = await settings.ReadAsync(ct).ConfigureAwait(false);
            snapshot = state.Selection is null
                ? await PinCurrentSelectionAsync(runId, current, ct).ConfigureAwait(false)
                : await BuildPinnedSnapshotAsync(state.Selection, current, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_cacheGate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested != 0 || _disposed, this);
                _runs[runId] = snapshot;
                if (_inflight.TryGetValue(runId, out var currentFlight)
                    && ReferenceEquals(currentFlight, completion.Task))
                    _inflight.Remove(runId);
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            lock (_cacheGate)
            {
                if (_inflight.TryGetValue(runId, out var currentFlight)
                    && ReferenceEquals(currentFlight, completion.Task))
                    _inflight.Remove(runId);
            }
        }

        if (failure is OperationCanceledException canceled)
            completion.TrySetCanceled(canceled.CancellationToken);
        else if (failure is not null)
            completion.TrySetException(failure);
        else
            completion.TrySetResult(snapshot!);
    }

    private async Task<ProviderBootstrapData> PinCurrentSelectionAsync(
        RunId runId, SettingsSnapshot current, CancellationToken ct)
    {
        current = ProviderIntentSettings.Apply(current,
            await selectionStore.GetIntentAsync(runId, ct).ConfigureAwait(false));
        var proposed = await DescribeSelectionAsync(current, ct).ConfigureAwait(false);
        proposed = await WithAdvisorAsync(proposed, current, runId, ct).ConfigureAwait(false);
        var stored = await selectionStore.PinAsync(runId, proposed, ct).ConfigureAwait(false);
        var pin = stored.Selection ?? throw new ProviderCatalogException("provider_selection_invalid");
        return await BuildPinnedSnapshotAsync(pin, current, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the advisor marker to a new root pin (ADR 0047). The advisor is the one the run's
    /// session selected; a session without one pins "not selected". An advisor that cannot be
    /// resolved now (signed out, catalog unavailable, model not listed) is pinned as
    /// unavailable: the run starts on its own selection, and a consultation reports
    /// <c>advisor_unavailable</c> instead of substituting another model.
    /// </summary>
    private async Task<AgentRunProviderSelection> WithAdvisorAsync(
        AgentRunProviderSelection proposed, SettingsSnapshot current, RunId runId, CancellationToken ct)
    {
        JsonObject marker;
        if (await selectionStore.GetRunSessionAdvisorAsync(runId, ct).ConfigureAwait(false) is not { } advisor)
            marker = AdvisorPolicy.NotSelectedMarker();
        else try
        {
            var described = await DescribeSelectionAsync(
                AdvisorPolicy.CandidateFor(current, advisor.Backend, advisor.Model), ct, requireCatalogModel: true)
                .ConfigureAwait(false);
            marker = AdvisorPolicy.Marker(described);
        }
        catch (Exception error) when (error is ProviderCatalogException or ArgumentException or HttpRequestException
            or IOException or UnknownProviderAliasException
            || (error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A slow or failing advisor provider must not stop the run from starting.
            marker = AdvisorPolicy.UnavailableMarker();
        }

        var options = JsonNode.Parse(proposed.RequestOptionsJson) as JsonObject
            ?? throw new ProviderCatalogException("provider_selection_invalid");
        options[AdvisorPolicy.PinField] = marker;
        return proposed with { RequestOptionsJson = options.ToJsonString() };
    }

    public async Task<AgentRunProviderSelection> DescribeSelectionAsync(
        SettingsSnapshot current, CancellationToken ct, bool requireCatalogModel = false)
    {
        var selected = await catalog.ResolveSelectionAsync(current, ct).ConfigureAwait(false);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        var options = ProviderReasoningPreferences.ResolveSaved(
            models,
            selected.Model,
            ProviderReasoningPreferences.EffortOverride() ?? ProviderReasoningPreferences.Read(current, selected));
        RequireRegisteredFixedModel(selected);
        // New pins leave maximum_output_tokens unset: the column keeps its released
        // 8,192 bound, and the output cap travels in output_token_reserve.
        int? maximumOutput = null;
        var modelMetadata = models.Catalog.FirstOrDefault(model =>
            string.Equals(model.Id, selected.Model, StringComparison.OrdinalIgnoreCase));
        // A catalog that publishes no limit (a live gateway model absent from Models.dev, or
        // a custom endpoint without lengths) assumes the shared current-model window. The
        // source says "assumed" so the pin records the assumption; a provider that serves
        // less rejects the oversized request with its own bounded context error, and the
        // run compacts under the same pin. Recording the assumption on the pin also gives
        // the GUI a real capacity instead of "unknown" for every new gateway model.
        var contextWindowTokens = modelMetadata?.ContextLength is { } published
            ? published
            : ContextWindowDefaults.UnknownContextWindowTokens;
        var contextWindowSource = modelMetadata?.ContextLength is null
            ? "assumed"
            : selected.Backend.StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase)
                ? "models.dev"
                // A named custom provider's catalog is the user's own list (ADR 0034).
                : selected.IsCustomProvider ? "user_setting" : "provider_catalog";
        var contextWindowVersion = modelMetadata?.ContextLength is null
            ? "assumed-v1"
            : models.LastSuccessUtc?.ToString("O") ?? "catalog-v1";
        // Runs always use native tool calling: every supported protocol defines it
        // (chat completions through function calls, Responses through function-call items,
        // Messages through tool-use blocks). A model that cannot call tools fails at the
        // provider with a bounded, named error instead of being downgraded to another protocol.
        // Every protocol lifts the reserve from the published model limit; reasoning models on
        // chat completions were truncated mid-call at the old fixed 8,192 just like Messages.
        var outputTokenReserve = maximumOutput
            ?? ModelOutputBudget.For(DocumentedOutputLimit(selected) ?? modelMetadata?.MaxOutputTokens, contextWindowTokens);
        if (ChatReasoningReplayPolicy.Supports(selected)) options[ChatReasoningReplayPolicy.PinField] = true;
        else if (await ChatReasoningReplayPolicy.DeclaredByCatalogAsync(catalog, selected, ct).ConfigureAwait(false))
            options[ChatReasoningReplayPolicy.CatalogPinField] = true;
        options[RuAgent.Protocol.AgentToolProfiles.PinField] = AgentToolProfilePin.SelectNew();
        if (await ImageInputPolicy.AcceptsAsync(catalog, selected, ct).ConfigureAwait(false))
        {
            options[ImageInputPolicy.PinField] = true;
            // Computer use needs screenshots, so it is pinned only with image input (ADR 0031).
            if (ComputerUsePolicy.IsEnabled(current)) options[ComputerUsePolicy.PinField] = true;
        }
        // Eligibility only; the run's policy decides whether it is used (ADR 0033).
        if (ProviderWebSearchPolicy.IsEnabled(current) && ProviderWebSearchPolicy.Supports(selected, options))
            options[ProviderWebSearchPolicy.PinField] = true;
        var proposed = new AgentRunProviderSelection(
            selected.Backend,
            selected.Alias,
            selected.Protocol,
            selected.BaseUri.AbsoluteUri,
            selected.Model,
            selected.Account,
            options.ToJsonString(),
            maximumOutput,
            selected.Model,
            AgentRunCallingModeCodec.Native,
            contextWindowTokens,
            contextWindowSource,
            contextWindowVersion,
            outputTokenReserve);
        if (requireCatalogModel && modelMetadata is null)
            throw new ProviderCatalogException("provider_model_unavailable");
        return proposed;
    }

    private async Task<ProviderBootstrapData> BuildPinnedSnapshotAsync(
        AgentRunProviderSelection pin,
        SettingsSnapshot current,
        CancellationToken ct)
    {
        var selected = ReadSelection(pin);
        JsonObject options;
        try
        {
            options = JsonNode.Parse(pin.RequestOptionsJson) as JsonObject
                ?? throw new ProviderCatalogException("provider_selection_invalid");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        AgentRunCallingModeCodec.RequireNative(pin.CallingMode);
        var replayChatReasoning = ChatReasoningReplayPolicy.TakePinnedCapability(options, selected);
        var acceptsImageInput = ImageInputPolicy.TakePinnedCapability(options);
        _ = ComputerUsePolicy.TakePinnedCapability(options);
        _ = ProviderWebSearchPolicy.TakePinnedCapability(options);
        _ = AdvisorPolicy.TakePinnedCapability(options);
        AgentToolProfilePin.ValidateAndRemove(options);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        _ = ProviderReasoningPreferences.Resolve(models, selected.Model, "default");
        return CreateSnapshot(selected, key, options, pin.MaximumOutputTokens ?? pin.OutputTokenReserve,
            useMaxCompletionTokens: selected.Alias == "openai"
                && selected.Protocol == ProviderProtocols.ChatCompletions,
            contextWindowTokens: pin.ContextWindowTokens,
            outputTokenReserve: pin.OutputTokenReserve,
            reducedEffortRequestOptions: ProviderReasoningPreferences.LowestEffortOptions(models, selected.Model, options),
            replayChatReasoning: replayChatReasoning,
            acceptsImageInput: acceptsImageInput,
            sessionHeaderName: ProviderOverrides.SessionHeaderName(selected.Alias));
    }

    /// <summary>
    /// The credential snapshot for one advisor consultation (ADR 0047). The advisor's pinned
    /// options carry no Ru markers; any that a user-named model's description added are dropped.
    /// A user-named effort replaces the saved one only when the model publishes it.
    /// </summary>
    internal async Task<ProviderBootstrapData> BuildAdvisorSnapshotAsync(
        AgentRunProviderSelection advisor, string? effort, CancellationToken ct)
    {
        var current = await settings.ReadAsync(ct).ConfigureAwait(false);
        var selected = ReadSelection(advisor);
        AgentRunCallingModeCodec.RequireNative(advisor.CallingMode);
        JsonObject options;
        try
        {
            options = JsonNode.Parse(advisor.RequestOptionsJson) as JsonObject
                ?? throw new ProviderCatalogException("provider_selection_invalid");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        foreach (var marker in options.Select(entry => entry.Key)
                     .Where(name => name.StartsWith("_ru_", StringComparison.Ordinal)
                         || name == RuAgent.Protocol.AgentToolProfiles.PinField)
                     .ToArray())
            options.Remove(marker);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        if (effort is not null)
        {
            if (models.Stale || models.Status != "ok")
                throw new AdvisorResolutionException(RuAgent.Harness.Core.AdvisorCodes.CatalogUnavailable);
            try { options = ProviderReasoningPreferences.Resolve(models, selected.Model, effort); }
            catch (ProviderCatalogException) { throw new AdvisorResolutionException(RuAgent.Harness.Core.AdvisorCodes.EffortUnsupported); }
        }

        return CreateSnapshot(selected, key, options, advisor.MaximumOutputTokens ?? advisor.OutputTokenReserve,
            useMaxCompletionTokens: selected.Alias == "openai" && selected.Protocol == ProviderProtocols.ChatCompletions,
            contextWindowTokens: advisor.ContextWindowTokens,
            outputTokenReserve: advisor.OutputTokenReserve,
            acceptsImageInput: false,
            sessionHeaderName: ProviderOverrides.SessionHeaderName(selected.Alias));
    }

    private async Task<string> ReadCredentialAsync(ProviderSelection selected, CancellationToken ct)
    {
        var key = selected.Account is null
            ? ""
            : await credentials.GetAsync("Ru", selected.Account, ct).ConfigureAwait(false) ?? "";
        if (selected.RequiresCredential && string.IsNullOrWhiteSpace(key))
            throw new ProviderCatalogException("catalog_unauthorized");
        // The key lives only in the vault, so the environment-seeded redactor never
        // knew it; seed it before the run can echo it into a reply or tool result.
        secrets?.AddStoredSecrets([key]);
        if (selected.Alias == "custom" || selected.IsCustomProvider)
            CustomProviders.RequireCredentialTransport(selected.CompletionUri, key);
        if ((selected.Alias is "openai" or "anthropic") && string.IsNullOrWhiteSpace(selected.Model))
            throw new ProviderCatalogException("model_unavailable");
        return key;
    }

    private static ProviderSelection ReadSelection(AgentRunProviderSelection pin)
    {
        var isSupportedProvider = pin.Backend switch
        {
            "Custom OpenAI" => pin.Alias == "custom"
                && pin.Protocol == ProviderProtocols.ChatCompletions,
            "OpenAI" => pin.Alias == "openai"
                && pin.Protocol == ProviderProtocols.OpenAiResponses,
            "Anthropic" => pin.Alias == "anthropic"
                && pin.Protocol == ProviderProtocols.AnthropicMessages,
            _ when CustomProviders.NormalizeBackend(pin.Backend) is { } custom => pin.Backend == custom
                && pin.Alias == custom
                && pin.Protocol is ProviderProtocols.ChatCompletions or ProviderProtocols.AnthropicMessages,
            // A Models.dev provider is pinned with the protocol its route resolved to (ADR 0052).
            _ => ProviderSelection.NormalizeModelsDevBackend(pin.Backend) is { } generic
                && pin.Alias == generic["models.dev:".Length..]
                && pin.Protocol is ProviderProtocols.ChatCompletions or ProviderProtocols.AnthropicMessages,
        };
        if (!Uri.TryCreate(pin.BaseUri, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.Query.Length != 0
            || baseUri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(pin.Model)
            || !string.Equals(pin.ModelLabel, pin.Model, StringComparison.Ordinal)
            || !isSupportedProvider)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        try
        {
            var expectedSelection = ResolveExpectedSelection(pin);
            if (pin.Alias is "openai" or "anthropic"
                && !string.Equals(baseUri.AbsoluteUri, expectedSelection.BaseUri.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new ProviderCatalogException("provider_selection_invalid");
            }

            if (!(pin.Alias == "custom" && pin.Account is null)
                && !string.Equals(pin.Account, expectedSelection.Account, StringComparison.Ordinal))
            {
                throw new ProviderCatalogException("provider_selection_invalid");
            }

            return new ProviderSelection(
                pin.Backend,
                pin.Alias,
                pin.Protocol,
                baseUri,
                pin.Model,
                pin.Account);
        }
        catch (ArgumentException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
    }

    private static ProviderSelection ResolveExpectedSelection(AgentRunProviderSelection pin)
    {
        // A custom provider's account follows from its id alone; its endpoint and
        // protocol are the pinned ones, and a deleted provider fails at the catalog.
        if (CustomProviders.NormalizeBackend(pin.Backend) is { } custom)
        {
            return new ProviderSelection(custom, custom, pin.Protocol, new Uri(pin.BaseUri), pin.Model,
                CustomProviders.CredentialAccount(CustomProviders.IdOf(custom)));
        }

        var settings = new SettingsSnapshot(
            new JsonObject(),
            pin.Backend,
            pin.Model,
            pin.Model,
            pin.BaseUri,
            pin.Model,
            pin.Model,
            CloudflareAccountId: "pin-validation");
        if (ProviderSelection.NormalizeModelsDevBackend(pin.Backend) is not null)
        {
            var provider = new ModelsDevProvider(pin.Alias, pin.Alias, pin.BaseUri, [], Protocol: pin.Protocol);
            return ProviderSelection.From(settings, [provider]);
        }

        return ProviderSelection.From(settings);
    }

    private static void ValidateCredentialRevision(string key, ProviderCatalogSnapshot models)
    {
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (revision != models.CredentialRevision)
            throw new ProviderCatalogException("provider_configuration_changed");
    }

    private static void RequireRegisteredFixedModel(ProviderSelection selected)
    {
        if (selected.Alias is ("openai" or "anthropic")
            && !ProviderCapabilityRegistry.TryGet(selected.Alias, selected.Model, out _))
            throw new ProviderCatalogException("model_unavailable");
    }

    private static int? DocumentedOutputLimit(ProviderSelection selected) =>
        selected.Alias == "anthropic" && ProviderCapabilityRegistry.TryGet(selected.Alias, selected.Model, out var capability)
            ? capability.DocumentedMaxOutputTokens
            : null;

    private static ProviderBootstrapData CreateSnapshot(
        ProviderSelection selected,
        string key,
        JsonObject options,
        int? maximumOutput,
        bool useMaxCompletionTokens = false,
        long? contextWindowTokens = null,
        int? outputTokenReserve = null,
        JsonObject? reducedEffortRequestOptions = null,
        bool replayChatReasoning = false,
        bool? acceptsImageInput = null,
        string? sessionHeaderName = null)
    {
        try
        {
            return new ProviderBootstrapData(
                selected.Backend,
                selected.Protocol,
                selected.CompletionUri,
                selected.Model,
                key,
                options,
                maximumOutput,
                useMaxCompletionTokens: useMaxCompletionTokens,
                contextWindowTokens: contextWindowTokens,
                outputTokenReserve: outputTokenReserve,
                reducedEffortRequestOptions: reducedEffortRequestOptions,
                replayChatReasoning: replayChatReasoning,
                acceptsImageInput: acceptsImageInput,
                sessionHeaderName: sessionHeaderName);
        }
        catch (ArgumentException)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
    }

    private async Task RemoveTerminalSnapshotsAsync(CancellationToken ct)
    {
        RunId[] ids;
        lock (_cacheGate) ids = _runs.Keys.ToArray();
        foreach (var id in ids)
        {
            if (!await isTerminal(id, ct).ConfigureAwait(false)) continue;
            lock (_cacheGate) _runs.Remove(id);
        }
    }

    public void Dispose()
    {
        var disposeTask = DisposeAsync().AsTask();
        bool completed;
        try { completed = disposeTask.Wait(SynchronousDisposeTimeout); }
        catch (AggregateException error)
        {
            var failure = error.InnerException ?? error;
            Trace.TraceError("Provider snapshot factory disposal failed ({0}).", failure.GetType().Name);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }

        if (!completed)
        {
            _ = disposeTask.ContinueWith(
                static task =>
                {
                    if (task.Exception is { } error)
                    {
                        Trace.TraceError("Provider snapshot factory deferred disposal failed ({0}).",
                            error.GetBaseException().GetType().Name);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Trace.TraceError("Provider snapshot factory disposal exceeded its {0}-second limit; cleanup remains queued.",
                SynchronousDisposeTimeout.TotalSeconds);
            return;
        }

        try { disposeTask.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Trace.TraceError("Provider snapshot factory disposal failed ({0}).", error.GetType().Name);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposeRequested, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task<ProviderBootstrapData>[] inflight;
        lock (_cacheGate) inflight = _inflight.Values.ToArray();
        try { await Task.WhenAll(inflight).ConfigureAwait(false); }
        catch (Exception) { /* In-flight callers receive their own load failures. */ }
        lock (_cacheGate)
        {
            _disposed = true;
            _runs.Clear();
            _inflight.Clear();
        }
    }
}

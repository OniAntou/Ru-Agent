using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
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
    Func<RunId, CancellationToken, Task<bool>> isTerminal) : IDisposable, IAsyncDisposable
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
        var proposed = await DescribeSelectionAsync(current, ct).ConfigureAwait(false);
        var stored = await selectionStore.PinAsync(runId, proposed, ct).ConfigureAwait(false);
        var pin = stored.Selection ?? throw new ProviderCatalogException("provider_selection_invalid");
        return await BuildPinnedSnapshotAsync(pin, current, ct).ConfigureAwait(false);
    }

    public async Task<AgentRunProviderSelection> DescribeSelectionAsync(
        SettingsSnapshot current, CancellationToken ct, bool requireCatalogModel = false)
    {
        var selected = await catalog.ResolveSelectionAsync(current, ct).ConfigureAwait(false);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        var options = ProviderReasoningPreferences.Resolve(
            models,
            selected.Model,
            ProviderReasoningPreferences.EffortOverride() ?? ProviderReasoningPreferences.Read(current, selected));
        RequireRegisteredFixedModel(selected);
        // New pins leave maximum_output_tokens unset: the column keeps its released
        // 8,192 bound, and the output cap travels in output_token_reserve.
        int? maximumOutput = null;
        var modelMetadata = models.Catalog.FirstOrDefault(model =>
            string.Equals(model.Id, selected.Model, StringComparison.OrdinalIgnoreCase));
        var contextWindowTokens = modelMetadata?.ContextLength;
        var contextWindowSource = contextWindowTokens is null
            ? "unknown"
            : selected.Backend.StartsWith("models.dev:", StringComparison.OrdinalIgnoreCase)
                ? "models.dev"
                : "provider_catalog";
        var contextWindowVersion = contextWindowTokens is null
            ? "unknown-v1"
            : models.LastSuccessUtc?.ToString("O") ?? "catalog-v1";
        // New runs always use native tool calling: every supported protocol defines it
        // (chat completions through function calls, Responses through function-call items,
        // Messages through tool-use blocks). The capability registry used to gate the two
        // block protocols to an exact declared model, which left every other model on a
        // weaker one-object selection protocol; a model that cannot call tools now fails
        // at the provider with a bounded, named error instead of being quietly downgraded.
        var toolCallingMode = ProviderToolCallingMode.Native;
        var outputTokenReserve = maximumOutput
            ?? (selected.Protocol == ProviderProtocols.AnthropicMessages
                ? AnthropicOutputBudget.For(DocumentedOutputLimit(selected) ?? modelMetadata?.MaxOutputTokens, contextWindowTokens)
                : AgentRunProviderSelection.DefaultOutputTokenReserve);
        if (ChatReasoningReplayPolicy.Supports(selected)) options[ChatReasoningReplayPolicy.PinField] = true;
        options[RuAgent.Protocol.AgentToolProfiles.PinField] = AgentToolProfilePin.SelectNew();
        if (await ImageInputPolicy.AcceptsAsync(catalog, selected, ct).ConfigureAwait(false))
        {
            options[ImageInputPolicy.PinField] = true;
            // Computer use needs screenshots, so it is pinned only with image input (ADR 0031).
            if (ComputerUsePolicy.IsEnabled(current)) options[ComputerUsePolicy.PinField] = true;
        }
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
            AgentRunCallingModeCodec.ToStoredValue(toolCallingMode),
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

        if (!AgentRunCallingModeCodec.TryParseStoredValue(pin.CallingMode, out var toolCallingMode))
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }
        var replayChatReasoning = ChatReasoningReplayPolicy.TakePinnedCapability(options, selected);
        // The image-input decision gates the read tool, not the provider request (ADR 0026).
        _ = ImageInputPolicy.TakePinnedCapability(options);
        _ = ComputerUsePolicy.TakePinnedCapability(options);
        AgentToolProfilePin.ValidateAndRemove(options);
        var key = await ReadCredentialAsync(selected, ct).ConfigureAwait(false);
        var models = await catalog.GetAsync(selected, current, false, ct).ConfigureAwait(false);
        ValidateCredentialRevision(key, models);
        _ = ProviderReasoningPreferences.Resolve(models, selected.Model, "default");
        return CreateSnapshot(selected, key, options, pin.MaximumOutputTokens ?? pin.OutputTokenReserve, toolCallingMode,
            allowNativeResponses: true,
            allowNativeAnthropic: true,
            useMaxCompletionTokens: selected.Alias == "openai"
                && selected.Protocol == ProviderProtocols.ChatCompletions,
            contextWindowTokens: pin.ContextWindowTokens,
            outputTokenReserve: pin.OutputTokenReserve,
            reducedEffortRequestOptions: ProviderReasoningPreferences.LowestEffortOptions(models, selected.Model, options),
            replayChatReasoning: replayChatReasoning);
    }

    private async Task<string> ReadCredentialAsync(ProviderSelection selected, CancellationToken ct)
    {
        var key = selected.Account is null
            ? ""
            : await credentials.GetAsync("Ru", selected.Account, ct).ConfigureAwait(false) ?? "";
        if (selected.RequiresCredential && string.IsNullOrWhiteSpace(key))
            throw new ProviderCatalogException("catalog_unauthorized");
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
            _ => ProviderSelection.NormalizeModelsDevBackend(pin.Backend) is { } generic
                && pin.Alias == generic["models.dev:".Length..]
                && pin.Protocol == ProviderProtocols.ChatCompletions,
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
            var provider = new ModelsDevProvider(pin.Alias, pin.Alias, pin.BaseUri, []);
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
        ProviderToolCallingMode toolCallingMode,
        bool allowNativeResponses = false,
        bool allowNativeAnthropic = false,
        bool useMaxCompletionTokens = false,
        long? contextWindowTokens = null,
        int? outputTokenReserve = null,
        JsonObject? reducedEffortRequestOptions = null,
        bool replayChatReasoning = false)
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
                toolCallingMode,
                allowNativeResponses,
                allowNativeAnthropic,
                useMaxCompletionTokens,
                contextWindowTokens,
                outputTokenReserve,
                reducedEffortRequestOptions,
                replayChatReasoning);
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

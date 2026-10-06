using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Reconciliation;
using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Harness;

public sealed class DynamicModelContextLimitProvider : IModelContextLimitProvider
{
    public const long ConservativeUnknownContextWindowTokens = 32_768;

    private readonly ISettingsStore? _settings;
    private readonly IAgentRunModelMetadataResolver? _metadataResolver;
    private (DateTimeOffset ExpiresAt, SettingsSnapshot Snapshot)? _cache;
    private readonly object _lock = new();

    public DynamicModelContextLimitProvider(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public DynamicModelContextLimitProvider(IAgentRunModelMetadataResolver metadataResolver)
    {
        _metadataResolver = metadataResolver ?? throw new ArgumentNullException(nameof(metadataResolver));
    }

    public async ValueTask<long?> MaxPromptTokensAsync(RunId runId, CancellationToken cancellationToken)
    {
        if (_metadataResolver is not null)
        {
            return await ResolvePinnedLimitAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            SettingsSnapshot? snapshot = null;
            lock (_lock)
            {
                if (_cache is { } cached && cached.ExpiresAt > now)
                {
                    snapshot = cached.Snapshot;
                }
            }

            if (snapshot is null)
            {
                snapshot = await _settings!.ReadAsync(cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _cache = (now.AddSeconds(5), snapshot);
                }
            }

            var model = ResolveActiveModel(snapshot);

            return ResolveLimitForModel(model, snapshot.Backend);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failing settings read must not fail the run: keep a conservative
            // window so compaction still bounds the prompt.
            return ConservativeUnknownContextWindowTokens;
        }
    }

    private async ValueTask<long> ResolvePinnedLimitAsync(RunId runId, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _metadataResolver!.ResolveAsync(runId, cancellationToken).ConfigureAwait(false);
            var window = metadata.ContextWindowTokens ?? ConservativeUnknownContextWindowTokens;
            return Math.Max(1, window - metadata.OutputTokenReserve);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderCatalogException error)
        {
            throw new ModelContextLimitResolutionException(error.Code, error);
        }
        catch (ModelContextLimitResolutionException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelContextLimitResolutionException("provider_metadata_unavailable", error);
        }
    }

    private static string ResolveActiveModel(SettingsSnapshot snapshot)
    {
        var backend = (snapshot.Backend ?? string.Empty).Trim().ToLowerInvariant();
        var active = backend switch
        {
            "custom openai" or "custom" or "custom openai api" => snapshot.CustomModel,
            "openai" or "openai api" => snapshot.OpenAiModel,
            "anthropic" or "anthropic claude" or "claude" => snapshot.AnthropicModel,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        // Legacy fallback: generic field first, then any provider-specific value.
        // The active backend field above always wins when present, so a stale
        // legacy Model cannot shadow a provider switch.
        return FirstNonEmpty(
            snapshot.Model,
            snapshot.OpenAiModel,
            snapshot.AnthropicModel,
            snapshot.CustomModel);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    public static long ResolveLimitForModel(string model, string? backend = null)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        if (m.Contains("claude") || m.Contains("anthropic"))
        {
            return 200_000;
        }

        if (m.Contains("gpt-4") || m.Contains("o1") || m.Contains("o3") || m.Contains("o4") || m.Contains("codex"))
        {
            return 128_000;
        }

        if (m.Contains("gemini"))
        {
            return 1_000_000;
        }

        if (m.Contains("deepseek") || m.Contains("qwen"))
        {
            return 65_536;
        }

        return ConservativeUnknownContextWindowTokens;
    }
}

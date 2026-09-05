using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Reconciliation;
using RuBackend.Business.Settings;

namespace RuBackend.Harness;

public sealed class DynamicModelContextLimitProvider : IModelContextLimitProvider
{
    private readonly ISettingsStore _settings;
    private (DateTimeOffset ExpiresAt, SettingsSnapshot Snapshot)? _cache;
    private readonly object _lock = new();

    public DynamicModelContextLimitProvider(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public long? MaxPromptTokens(RunId runId)
    {
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
                snapshot = _settings.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                lock (_lock)
                {
                    _cache = (now.AddSeconds(5), snapshot);
                }
            }

            var model = ResolveActiveModel(snapshot);

            return ResolveLimitForModel(model, snapshot.Backend);
        }
        catch
        {
            return 32_768;
        }
    }

    private static string ResolveActiveModel(SettingsSnapshot snapshot)
    {
        var backend = (snapshot.Backend ?? string.Empty).Trim().ToLowerInvariant();
        var active = backend switch
        {
            "opencode zen" or "opencode" or "opencode zen api" or "opencode api" => snapshot.OpenCodeModel,
            "kilo gateway" or "kilo" or "kilo gateway api" or "kilo api" => snapshot.KiloModel,
            "ollama" or "ollama api" => snapshot.OllamaModel,
            "lm studio" or "lmstudio" or "lm studio api" => snapshot.LmStudioModel,
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
            snapshot.OpenCodeModel,
            snapshot.KiloModel,
            snapshot.OpenAiModel,
            snapshot.AnthropicModel,
            snapshot.OllamaModel,
            snapshot.LmStudioModel,
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

        return 32_768;
    }
}

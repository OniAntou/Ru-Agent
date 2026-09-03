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

            var model = snapshot.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = snapshot.OpenAiModel ?? snapshot.AnthropicModel ?? snapshot.OllamaModel ?? string.Empty;
            }

            return ResolveLimitForModel(model, snapshot.Backend);
        }
        catch
        {
            return 32_768;
        }
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

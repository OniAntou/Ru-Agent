using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Model;

/// <summary>Provider-anchored input estimates with exact envelope and history-prefix guards.</summary>
/// <remarks>
/// Beside the per-conversation anchor it keeps one calibration per route and model:
/// how many provider tokens a heuristic token turned out to be. Bytes/4 undercounts
/// some tokenizers by a quarter or more (DeepSeek on a long coding run reported 615k
/// against a 467k estimate), and the anchor is dropped after compaction, an image, a
/// rewritten prefix or 30 idle minutes. Without calibration that fallback moved the
/// 80% compaction trigger to roughly the full window, so the provider rejected the
/// request before Ru compacted. The ratio only ever raises an estimate (never below
/// the raw heuristic) and is learned only from requests large enough to be stable.
/// </remarks>
internal sealed class RequestTokenAnchor
{
    private sealed record Anchor(string Envelope, string Prefix, int Count, long Bytes, long Tokens, long At);
    private readonly Dictionary<string, Anchor> _anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _ratios = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private const int MaximumScopes = 256;
    private const int MaximumCalibrations = 64;
    internal const double MaximumRatio = 2.0;
    internal const long MinimumCalibrationTokens = 4096;

    public long Estimate(string scope, string body, string route, long heuristic, string? model = null)
    {
        var current = Describe(body, route);
        lock (_gate)
        {
            var ratio = model is null ? 1.0 : _ratios.GetValueOrDefault(Calibration(route, model), 1.0);
            var calibrated = Scale(heuristic, ratio);
            if (current is null || !_anchors.TryGetValue(scope, out var anchor)) return calibrated;
            if (Stopwatch.GetElapsedTime(anchor.At) > TimeSpan.FromMinutes(30) || anchor.Envelope != current.Value.Envelope
                || anchor.Count > current.Value.Messages.Count || anchor.Prefix != HashPrefix(current.Value.Messages, anchor.Count))
            { _anchors.Remove(scope); return calibrated; }
            var delta = Math.Max(0, current.Value.Bytes - anchor.Bytes);
            return Math.Max(heuristic, anchor.Tokens + Scale((delta + 3) / 4, ratio));
        }
    }

    private static long Scale(long tokens, double ratio) =>
        ratio <= 1.0 ? tokens : (long)Math.Min(long.MaxValue / 2, Math.Ceiling(tokens * ratio));

    private static string Calibration(string route, string model) => route + "|" + model;

    public void Observe(string scope, string body, string route, string model, ModelUsage? usage)
    {
        var current = Describe(body, route);
        var input = usage?.InputTokensIncludeCache switch
        {
            true => usage.InputTokens,
            false when usage.InputTokens is not null => (long?)usage.InputTokens + (usage.CacheReadInputTokens ?? 0L) + (usage.CacheWriteInputTokens ?? 0L),
            _ => null,
        };
        lock (_gate)
        {
            if (current is null || input is null || usage?.Model != model) { _anchors.Remove(scope); return; }
            // The images-free body is the whole heuristic: the same bytes/4 MeasurePreparedRequest applies.
            var heuristic = (Encoding.UTF8.GetByteCount(body) + 3L) / 4;
            if (heuristic >= MinimumCalibrationTokens)
            {
                var key = Calibration(route, model);
                if (_ratios.Count >= MaximumCalibrations && !_ratios.ContainsKey(key)) _ratios.Clear();
                _ratios[key] = Math.Clamp((double)input.Value / heuristic, 1.0, MaximumRatio);
            }
            if (_anchors.Count >= MaximumScopes && !_anchors.ContainsKey(scope))
                _anchors.Remove(_anchors.MinBy(pair => pair.Value.At).Key);
            _anchors[scope] = new Anchor(current.Value.Envelope, HashPrefix(current.Value.Messages, current.Value.Messages.Count),
                current.Value.Messages.Count, current.Value.Bytes, input.Value, Stopwatch.GetTimestamp());
        }
    }

    private static (string Envelope, JsonArray Messages, long Bytes)? Describe(string body, string route)
    {
        var root = JsonNode.Parse(body) as JsonObject;
        var key = root?["messages"] is JsonArray ? "messages" : "input";
        if (root?[key] is not JsonArray messages || messages.Count == 0) return null;
        var bytes = Encoding.UTF8.GetByteCount(messages.ToJsonString());
        root.Remove(key);
        return (Hash(route + "\n" + root.ToJsonString()), messages, bytes);
    }

    private static string HashPrefix(JsonArray messages, int count) =>
        Hash(string.Join("\n", messages.Take(count).Select(message => message?.ToJsonString() ?? "null")));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

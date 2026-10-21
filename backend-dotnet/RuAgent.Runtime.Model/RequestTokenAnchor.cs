using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuAgent.Runtime.Model;

/// <summary>Provider-anchored input estimates with exact envelope and history-prefix guards.</summary>
internal sealed class RequestTokenAnchor
{
    private sealed record Anchor(string Envelope, string Prefix, int Count, long Bytes, long Tokens, long At);
    private readonly Dictionary<string, Anchor> _anchors = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private const int MaximumScopes = 256;

    public long Estimate(string scope, string body, string route, long heuristic)
    {
        var current = Describe(body, route);
        if (current is null) return heuristic;
        lock (_gate)
        {
            if (!_anchors.TryGetValue(scope, out var anchor)) return heuristic;
            if (Stopwatch.GetElapsedTime(anchor.At) > TimeSpan.FromMinutes(30) || anchor.Envelope != current.Value.Envelope
                || anchor.Count > current.Value.Messages.Count || anchor.Prefix != HashPrefix(current.Value.Messages, anchor.Count))
            { _anchors.Remove(scope); return heuristic; }
            var delta = Math.Max(0, current.Value.Bytes - anchor.Bytes);
            return Math.Max(heuristic, anchor.Tokens + (delta + 3) / 4);
        }
    }

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

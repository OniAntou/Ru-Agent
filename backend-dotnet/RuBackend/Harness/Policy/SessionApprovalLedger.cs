using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;

namespace RuBackend.Harness.Policy;

/// <summary>
/// Host-side session ledger of user-approved intents. One allow decision
/// remembers the exact canonical intent for the Agent session, so a later call
/// with the same intent evaluates as allowed without another prompt. In-memory
/// only: a host restart or a session delete forgets every entry (fail closed),
/// and each entry expires like the approval ledger it mirrors.
/// </summary>
internal static class SessionApprovalLedger
{
    internal const int MaxEntriesPerSession = 64;
    internal static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);

    internal sealed record Entry(string Key, PolicyMatcher Matcher, DateTimeOffset ExpiresAt);

    // One lock guards the whole ledger. Sessions hold at most 64 entries, and a
    // single owner removes the race in which pruning an empty session dropped an
    // entry another thread was adding to it.
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, Entry>> Sessions = new(StringComparer.Ordinal);

    internal static void Remember(string sessionId, PolicyMatcher matcher, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(matcher);

        var key = ComputeKey(matcher);
        lock (Gate)
        {
            Prune(now);
            if (!Sessions.TryGetValue(sessionId, out var entries))
            {
                entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                Sessions[sessionId] = entries;
            }

            entries[key] = new Entry(key, matcher, now + EntryLifetime);
            while (entries.Count > MaxEntriesPerSession)
            {
                var oldest = entries.Values
                    .OrderBy(item => item.ExpiresAt)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .First();
                entries.Remove(oldest.Key);
            }
        }
    }

    internal static IReadOnlyList<Entry> Entries(string sessionId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (Gate)
        {
            if (!Sessions.TryGetValue(sessionId, out var entries))
            {
                return Array.Empty<Entry>();
            }

            foreach (var expired in entries.Values.Where(item => item.ExpiresAt <= now).ToArray())
            {
                entries.Remove(expired.Key);
            }

            if (entries.Count == 0)
            {
                Sessions.Remove(sessionId);
                return Array.Empty<Entry>();
            }

            return entries.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Materializes the session's remembered intents as run-scope allow rules
    /// on top of a durable snapshot. The returned snapshot is deterministic for
    /// the same ledger contents, so repeated per-drive builds keep one identity
    /// until the ledger changes. The version is strictly above the durable one,
    /// but two different ledgers of the same size can derive the same version,
    /// so pending approvals and authorization tokens also bind the snapshot hash.
    /// </summary>
    internal static PolicySnapshot? Apply(PolicySnapshot? snapshot, string sessionId, DateTimeOffset now)
    {
        if (snapshot is null)
        {
            return null;
        }

        var entries = Entries(sessionId, now);
        if (entries.Count == 0)
        {
            return snapshot;
        }

        var rules = new List<PolicyRule>(snapshot.OrderedRules.Count + entries.Count);
        rules.AddRange(snapshot.OrderedRules);
        var creationOrder = rules.Count == 0 ? 0 : rules.Max(rule => rule.CreationOrder) + 1;
        foreach (var entry in entries)
        {
            rules.Add(new PolicyRule(
                RuleIdFor(entry.Key),
                PolicyRuleScope.Run,
                PolicyDecision.Allow,
                entry.Matcher,
                creationOrder++,
                entry.ExpiresAt));
        }

        var version = Math.Max(snapshot.Version + 1, rules.Max(rule => rule.CreationOrder) + 1);
        return PolicySnapshotCanonicalizer.Create(version, snapshot.Preset, snapshot.Workspace, rules, now);
    }

    internal static void ClearSession(string sessionId)
    {
        lock (Gate) Sessions.Remove(sessionId);
    }

    internal static void ClearForTests()
    {
        lock (Gate) Sessions.Clear();
    }

    // Callers hold Gate.
    private static void Prune(DateTimeOffset now)
    {
        foreach (var (sessionId, entries) in Sessions.ToArray())
        {
            foreach (var expired in entries.Values.Where(item => item.ExpiresAt <= now).ToArray())
            {
                entries.Remove(expired.Key);
            }

            if (entries.Count == 0)
            {
                Sessions.Remove(sessionId);
            }
        }
    }

    private static string ComputeKey(PolicyMatcher matcher) =>
        JsonSerializer.Serialize(new[]
        {
            matcher.ToolId ?? string.Empty,
            matcher.ToolVersion ?? string.Empty,
            matcher.CanonicalPathPattern ?? string.Empty,
            matcher.ResolvedExecutable ?? string.Empty,
            string.Join('\u001e', matcher.ArgvPattern ?? Array.Empty<string>()),
            matcher.CwdPattern ?? string.Empty,
            matcher.OperationClass ?? string.Empty,
            matcher.NetworkIntent ?? string.Empty,
        });

    private static PolicyRuleId RuleIdFor(string key) =>
        new("rule_" + Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant()[..32]);
}

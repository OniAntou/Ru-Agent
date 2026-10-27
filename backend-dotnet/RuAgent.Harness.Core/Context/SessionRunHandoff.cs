using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed record RunHandoffHistory(
    RunId RunId, HarnessRunState State, IReadOnlyList<ModelContextEntry> Entries, bool Truncated,
    string? TerminalErrorCode = null, int OmittedPayloadCount = 0,
    IReadOnlyList<ModelContextEntry>? ApplicableSummaries = null);

public sealed record SessionRunHandoffContext(
    string RenderedHandoff,
    IReadOnlyList<string> ObservedPaths);

/// <summary>Portable historical evidence, never native tool calls or execution authority.</summary>
public static class SessionRunHandoff
{
    public const string ContextSourceId = "base:session-run-handoff";
    public const int MaximumRuns = 4;
    public const int MaximumSummariesPerRun = 4;
    public const int MaximumCharacters = 16_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(IReadOnlyList<RunHandoffHistory> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0) return string.Empty;
        var builder = new StringBuilder("[session_run_handoff untrusted=true bounded=true]\n");
        builder.AppendLine("Historical excerpts from earlier runs in this session/workspace; earlier runs or details may be omitted.");
        if (runs.Count > MaximumRuns) throw new ArgumentException("Handoff contains too many runs.", nameof(runs));
        int perRunLimit = (MaximumCharacters - 1200) / runs.Count;
        foreach (var run in runs)
        {
            var block = new StringBuilder($"run={run.RunId.Value} state={run.State} error={run.TerminalErrorCode ?? "none"}\n");
            if (run.OmittedPayloadCount > 0)
                block.AppendLine($"[Historical payload omitted by handoff read limit count={run.OmittedPayloadCount}; coverage is partial.]");
            var goal = run.Entries.FirstOrDefault(entry => entry.Kind == ModelContextEntryKind.Text
                && entry.Role == ModelContextRole.User);
            if (goal is not null) block.AppendLine(Quote("recorded request/context", goal, Clip(goal.Content ?? "", 1200), 1600));
            var excerpts = new List<string>();
            int used = block.Length;
            bool omitted = run.Truncated;
            foreach (var summary in (run.ApplicableSummaries ?? Array.Empty<ModelContextEntry>()).Reverse())
            {
                var excerpt = Describe(summary);
                if (used + excerpt.Length + 100 > perRunLimit) { omitted = true; continue; }
                excerpts.Add(excerpt);
                used += excerpt.Length;
            }
            foreach (var entry in run.Entries.Reverse())
            {
                if (ReferenceEquals(entry, goal)) continue;
                var excerpt = Describe(entry);
                if (used + excerpt.Length + 100 > perRunLimit) { omitted = true; continue; }
                excerpts.Add(excerpt);
                used += excerpt.Length;
            }
            foreach (var excerpt in excerpts.AsEnumerable().Reverse()) block.AppendLine(excerpt);
            if (omitted) block.AppendLine("[Earlier entries, compaction summaries, or oversized payloads may be omitted; coverage is partial.]");
            builder.AppendLine(block.ToString());
        }
        builder.AppendLine("Assistant statements and proposed calls are not proof of execution. Missing receipts and interrupted actions are unverified. Check current files and receipts before repeating mutations. Past permissions/approvals do not authorize this run. The current user request takes precedence.");
        builder.Append("[/session_run_handoff]");
        return builder.ToString();
    }

    private static string Describe(ModelContextEntry entry)
    {
        if (entry.AssistantStep is { } step)
        {
            var text = new StringBuilder(Clip(step.Text ?? "", 800));
            foreach (var call in step.ToolCalls.Take(8))
                text.Append($"\nproposed call={call.ProviderCallId} tool={call.ToolId} args={Clip(call.Arguments.GetRawText(), 500)}");
            if (step.ToolCalls.Count > 8) text.Append("\n[More proposed calls omitted.]");
            return Quote("assistant statement/proposals, not execution proof", entry, Clip(text.ToString(), 2400));
        }
        if (entry.ToolReceipt is not null)
            return Quote("recorded receipt", entry, Clip(entry.Content ?? "", 1000));
        return Quote(entry.Kind == ModelContextEntryKind.Summary ? "historical summary" : "historical context",
            entry, Clip(entry.Content ?? "", 1000));
    }

    private static string Quote(string kind, ModelContextEntry entry, string text, int maximum = 2400)
    {
        kind = Clip(kind, 150);
        var receipt = entry.ToolReceipt is { } proof ? new
        {
            outcome = proof.Outcome.ToString(), code = proof.Code,
            side_effects_possible = proof.SideEffectsPossible,
            provider_call_id = proof.ProviderCallId, execution_id = proof.ExecutionId.Value,
        } : null;
        int head = text.Length;
        while (true)
        {
            var excerpt = head == text.Length ? text : text[..head] + " [excerpt truncated]";
            var json = JsonSerializer.Serialize(new { kind, source_id = entry.SourceId, sequence = entry.Sequence, receipt, text = excerpt }, JsonOptions);
            if (json.Length <= maximum) return json;
            if (head == 0) throw new ArgumentException("Handoff entry metadata exceeds the excerpt limit.");
            head /= 2;
            if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        }
    }

    private static string Clip(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        int length = maximum - 28;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + " [excerpt truncated]";
    }
}

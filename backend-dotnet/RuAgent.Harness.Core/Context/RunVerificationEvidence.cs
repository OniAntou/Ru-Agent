using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public enum VerificationCheckStatus { Pending, Passed, Failed, Stale }

public sealed record VerificationCheckEvidence(string Identity, string ProviderCallId,
    string? ExecutionId, int ExpectedExitCode, bool IsVerification,
    VerificationCheckStatus Status, string Code, string Command);

/// <summary>Host-derived original receipt evidence; survives compaction, grants no authority.</summary>
public sealed partial class RunVerificationEvidence
{
    public const int MaximumChecks = 256;
    public IReadOnlyList<VerificationCheckEvidence> Checks { get; }
    public bool Overflowed { get; }
    private readonly HashSet<string> _expectedFailures;

    private RunVerificationEvidence(IEnumerable<VerificationCheckEvidence> checks,
        HashSet<string> expectedFailures, bool overflowed)
    {
        Checks = new ReadOnlyCollection<VerificationCheckEvidence>(checks.ToArray());
        _expectedFailures = expectedFailures;
        Overflowed = overflowed;
    }

    public string? BlockingCode => Overflowed ? "verification_tracking_limit"
        : Checks.FirstOrDefault(check => check.Status != VerificationCheckStatus.Passed)?.Code;

    public bool IsExpectedFailure(ToolExecutionReceipt receipt) =>
        receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero"
        && receipt.ProviderCallId is { } id && _expectedFailures.Contains(ReceiptIdentity(receipt, id));

    public string? DescribePending()
    {
        var pending = Checks.Where(check => check.Status != VerificationCheckStatus.Passed).ToArray();
        if (pending.Length == 0 && !Overflowed) return null;
        return "Outstanding declared checks: " + string.Join(", ", pending.Take(8)
            .Select(check => $"{check.ProviderCallId} ({check.Code}): {check.Command}"))
            + (pending.Length > 8 ? $"; {pending.Length - 8} more" : "")
            + (Overflowed ? "; verification_tracking_limit" : "")
            + ". Inspecting logs does not satisfy a check. Rerun the same check after repairs or report [BLOCKED].";
    }

    public static RunVerificationEvidence Project(IEnumerable<ModelContextEntry> originalRunEntries)
    {
        var checks = new Dictionary<string, VerificationCheckEvidence>(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        bool overflowed = false;
        foreach (var entry in originalRunEntries.OrderBy(entry => entry.Sequence))
        {
            if (entry.Kind == ModelContextEntryKind.AssistantStep)
            {
                calls.Clear();
                foreach (var call in entry.AssistantStep!.ToolCalls) calls[call.ProviderCallId] = call;
                continue;
            }
            if (entry.ToolReceipt is not { ProviderCallId: { } providerId } receipt
                || !calls.TryGetValue(providerId, out var proposed)) continue;
            // Most turns only inspect files. With no tracked check there is
            // nothing for a nonterminal mutation to invalidate or resolve.
            if (checks.Count == 0 && proposed.ToolId != "bash") continue;
            var args = proposed.Arguments;
            string purpose = StringArg(args, "purpose") ?? "command";
            string mode = StringArg(args, "mode") ?? "run";
            bool terminal = proposed.ToolId == "bash" && proposed.ToolVersion == "1.0.0";
            if (terminal && mode is "read_output" or "kill")
            {
                var executionId = StringArg(args, "execution_id");
                foreach (var prior in checks.Values.Where(check => executionId is not null
                    && check.ExecutionId == executionId && check.Status == VerificationCheckStatus.Pending).ToArray())
                    checks[prior.Identity] = Assess(prior, receipt, entry.Content, providerId, mode == "kill");
                continue;
            }
            bool effect = receipt.Outcome == ToolExecutionOutcome.Succeeded || receipt.SideEffectsPossible
                || receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown;
            bool mutation = proposed.ToolId is "write" or "edit" or "agent_apply"
                || !terminal && receipt.SideEffectsPossible || terminal && purpose == "command";
            if (mutation && effect)
                foreach (var check in checks.Values.Where(check => check.IsVerification
                    && check.Status == VerificationCheckStatus.Passed).ToArray())
                    checks[check.Identity] = check with { Status = VerificationCheckStatus.Stale, Code = "verification_stale" };
            if (!terminal) continue;
            int expectedExit = 0;
            bool negative = purpose == "expected_failure" && TryExpectedExit(args, out expectedExit);
            if (!negative) expectedExit = 0;
            string identity = CommandIdentity(args, expectedExit);
            bool tracked = checks.ContainsKey(identity);
            bool actualProcessFailure = receipt.Code is "process_exit_nonzero" or "process_timed_out" or "process_cancelled";
            if (!tracked && purpose != "verification" && !negative && !actualProcessFailure) continue;
            if (!tracked && checks.Count >= MaximumChecks) { overflowed = true; continue; }
            var initial = new VerificationCheckEvidence(identity, providerId, null, expectedExit,
                !negative, VerificationCheckStatus.Pending, "verification_pending", DescribeCommand(args));
            var assessed = Assess(initial, receipt, entry.Content, providerId, false);
            checks[identity] = assessed;
            if (negative && assessed.Status == VerificationCheckStatus.Passed
                && receipt.Outcome == ToolExecutionOutcome.Failed)
            {
                if (expected.Count < MaximumChecks) expected.Add(ReceiptIdentity(receipt, providerId));
                else overflowed = true;
            }
        }
        return new(checks.Values, expected, overflowed);
    }

    private static string CommandIdentity(JsonElement args, int expectedExit)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (string name in new[] { "executable", "script", "shell" })
                if (args.TryGetProperty(name, out var value)) { writer.WritePropertyName(name); value.WriteTo(writer); }
            writer.WritePropertyName("arguments");
            if (args.TryGetProperty("arguments", out var arguments)) arguments.WriteTo(writer);
            else { writer.WriteStartArray(); writer.WriteEndArray(); }
            writer.WriteString("mode", StringArg(args, "mode") ?? "run");
            writer.WriteString("working_directory", StringArg(args, "working_directory") ?? ".");
            writer.WriteNumber("expected_exit_code", expectedExit);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static string? StringArg(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReceiptIdentity(ToolExecutionReceipt receipt, string providerId) =>
        receipt.ExecutionId.Value + "|" + receipt.ToolCallId.Value + "|" + providerId;

    private static string DescribeCommand(JsonElement args)
    {
        string text = StringArg(args, "script") ?? StringArg(args, "executable") ?? "terminal check";
        if (args.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            text += " " + arguments.GetRawText();
        text = text.Replace('\0', ' ');
        if (text.Length <= 512) return text;
        var length = char.IsHighSurrogate(text[511]) ? 511 : 512;
        return text[..length] + " [command excerpt]";
    }

    private static bool TryExpectedExit(JsonElement args, out int code)
    {
        code = 0;
        return args.TryGetProperty("expected_exit_code", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out code) && code is >= 1 and <= 255;
    }
}

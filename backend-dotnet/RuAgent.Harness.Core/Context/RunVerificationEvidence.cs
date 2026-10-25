using System.Collections.ObjectModel;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public enum VerificationCheckStatus { Pending, Passed, Failed, Stale }

public sealed record VerificationCheckEvidence(string Identity, string ProviderCallId,
    string? ExecutionId, int ExpectedExitCode, bool IsVerification,
    VerificationCheckStatus Status, string Code, string Command, bool InvalidatedDuringExecution = false);

/// <summary>Host-derived original receipt evidence; survives compaction, grants no authority.</summary>
public sealed partial class RunVerificationEvidence
{
    public const int MaximumChecks = 256;
    public IReadOnlyList<VerificationCheckEvidence> Checks { get; }
    public bool Overflowed { get; }
    public bool HasMutations { get; }
    private readonly HashSet<string> _expectedFailures;
    private readonly string? _programFailure;

    private RunVerificationEvidence(IEnumerable<VerificationCheckEvidence> checks,
        HashSet<string> expectedFailures, bool overflowed, bool hasMutations, string? programFailure = null)
    {
        Checks = new ReadOnlyCollection<VerificationCheckEvidence>(checks.ToArray());
        _expectedFailures = expectedFailures;
        Overflowed = overflowed;
        HasMutations = hasMutations;
        _programFailure = programFailure;
    }

    public string? BlockingCode => _programFailure ?? (Overflowed ? "verification_tracking_limit"
        : Checks.FirstOrDefault(check => (check.IsVerification || check.ExpectedExitCode != 0)
            && check.Status != VerificationCheckStatus.Passed)?.Code);

    public bool IsExpectedFailure(ToolExecutionReceipt receipt) =>
        receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero"
        && receipt.ProviderCallId is { } id && _expectedFailures.Contains(ReceiptIdentity(receipt, id));

    public string? DescribePending()
    {
        if (_programFailure is not null) return _programFailure + ": program effects are not fully recorded. Inspect the workspace; do not repeat uncertain effects. Report [BLOCKED].";
        var pending = Checks.Where(check => (check.IsVerification || check.ExpectedExitCode != 0)
            && check.Status != VerificationCheckStatus.Passed).ToArray();
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
        bool overflowed = false;
        bool hasMutations = false;
        string? programFailure = null;
        foreach (var (entry, proposed) in ExpandProgramEvidence(originalRunEntries, () =>
            { programFailure = "program_evidence_unavailable"; hasMutations = true; InvalidateChecks(checks); }))
        {
            var receipt = entry.ToolReceipt!;
            var providerId = receipt.ProviderCallId!;
            bool terminal = proposed.ToolId is "bash" or "powershell" or "run_process" or "terminal_read" or "terminal_stop"
                && proposed.ToolVersion == "1.0.0";
            bool effect = receipt.Outcome == ToolExecutionOutcome.Succeeded || receipt.SideEffectsPossible
                || receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown;
            if (!terminal)
            {
                if (effect && (proposed.ToolId is "write" or "edit" or "write_file" or "edit_file" or "file_delete" or "file_restore" or "apply_patch" or "agent_apply" || receipt.SideEffectsPossible))
                {
                    hasMutations = true;
                    InvalidateChecks(checks);
                }
                continue;
            }
            var args = proposed.Arguments;
            if (RuAgent.Protocol.CodingToolArguments.AdapterToolId(proposed.ToolId) == "bash")
            {
                // Even a malformed, rejected call must retain its declared verification intent.
                var displayArguments = System.Text.Json.Nodes.JsonNode.Parse(args.GetRawText())!.AsObject();
                displayArguments.Remove("op");
                displayArguments.Remove("mode");
                displayArguments.Remove("shell");
                args = System.Text.Json.JsonSerializer.SerializeToElement(RuAgent.Protocol.CodingToolArguments.ToAdapterArguments(
                    proposed.ToolId, displayArguments));
            }
            string purpose = StringArg(args, "purpose") ?? "command";
            string mode = StringArg(args, "mode") ?? "run";
            if (mode is "read_output" or "kill")
            {
                var executionId = StringArg(args, "execution_id");
                foreach (var prior in checks.Values.Where(check => executionId is not null
                    && check.ExecutionId == executionId && check.Status == VerificationCheckStatus.Pending).ToArray())
                    checks[prior.Identity] = Assess(prior, receipt, entry.Content, providerId, mode == "kill");
                continue;
            }
            if (purpose == "command" && effect)
            {
                hasMutations = true;
                InvalidateChecks(checks);
            }
            int expectedExit = 0;
            bool negative = purpose == "expected_failure" && TryExpectedExit(args, out expectedExit);
            if (!negative) expectedExit = 0;
            string rawIdentity = CommandIdentity(args, expectedExit);
            string identity = ReceiptIdentity(entry.Content, expectedExit) ?? rawIdentity;
            // Upgrade a legacy check only after the exact original invocation is
            // observed again with authority metadata, never by guessing old paths.
            if (identity != rawIdentity && checks.Remove(rawIdentity, out var legacy))
                checks[identity] = legacy with { Identity = identity };
            bool tracked = checks.ContainsKey(identity);
            bool actualProcessFailure = receipt.Code is "process_exit_nonzero" or "process_timed_out" or "process_cancelled";
            if (!tracked && purpose != "verification" && !negative && !actualProcessFailure) continue;
            if (!tracked && checks.Count >= MaximumChecks) { overflowed = true; continue; }
            var initial = new VerificationCheckEvidence(identity, providerId, null, expectedExit,
                purpose == "verification" || tracked && checks[identity].IsVerification,
                VerificationCheckStatus.Pending, "verification_pending", DescribeCommand(args));
            var assessed = Assess(initial, receipt, entry.Content, providerId, false);
            checks[identity] = assessed;
            if (negative && assessed.Status == VerificationCheckStatus.Passed
                && receipt.Outcome == ToolExecutionOutcome.Failed)
            {
                if (expected.Count < MaximumChecks) expected.Add(ReceiptIdentity(receipt, providerId));
                else overflowed = true;
            }
        }
        return new(checks.Values, expected, overflowed, hasMutations, programFailure);
    }

    private static IEnumerable<(ModelContextEntry Entry, ModelToolCall Call)> ExpandProgramEvidence(
        IEnumerable<ModelContextEntry> source, Action unknown)
    {
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var entry in source.OrderBy(item => item.Sequence))
        {
            if (entry.Kind == ModelContextEntryKind.AssistantStep)
            {
                calls.Clear();
                foreach (var call in entry.AssistantStep!.ToolCalls) calls[call.ProviderCallId] = call;
            }
            if (entry.ToolReceipt is not { ProviderCallId: { } providerId } receipt || !calls.TryGetValue(providerId, out var proposed)) continue;
            if (proposed.ToolId != "run_code") { yield return (entry, proposed); continue; }
            if (!ProgramReceiptTrace.TryRead(entry.Content, providerId, out var trace))
            {
                if (receipt.SideEffectsPossible || receipt.Outcome is ToolExecutionOutcome.Succeeded or ToolExecutionOutcome.OutcomeUnknown) unknown();
                continue;
            }
            foreach (var nested in trace)
            {
                var nestedReceipt = new ToolExecutionReceipt(receipt.ExecutionId, receipt.ToolCallId, nested.Outcome,
                    nested.Code, null, nested.SideEffectsPossible, providerCallId: nested.Call.ProviderCallId);
                yield return (ModelContextEntry.ToolResult(nested.Call.ProviderCallId, entry.Sequence, nestedReceipt, nested.Output.GetRawText()), nested.Call);
            }
        }
    }

    private static void InvalidateChecks(Dictionary<string, VerificationCheckEvidence> checks)
    {
        foreach (var check in checks.Values.Where(check => check.IsVerification
            && check.Status is VerificationCheckStatus.Passed or VerificationCheckStatus.Pending).ToArray())
            checks[check.Identity] = check.Status == VerificationCheckStatus.Pending
                ? check with { InvalidatedDuringExecution = true }
                : check with { Status = VerificationCheckStatus.Stale, Code = "verification_stale" };
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
        text = "cwd=" + (StringArg(args, "working_directory") ?? ".")
            + "; mode=" + (StringArg(args, "mode") ?? "run") + "; " + text;
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

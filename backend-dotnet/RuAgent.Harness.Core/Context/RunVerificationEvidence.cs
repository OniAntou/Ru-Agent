using System.Collections.ObjectModel;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public enum VerificationCheckStatus { Pending, Passed, Failed, Stale }

public sealed record VerificationCheckEvidence(string Identity, string ProviderCallId,
    string? ExecutionId, int ExpectedExitCode, bool IsVerification,
    VerificationCheckStatus Status, string Code, string Command, bool InvalidatedDuringExecution = false,
    int? ObservedExitCode = null);

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

    public string? DescribeExitMismatch()
    {
        var mismatches = Checks.Where(IsConfirmedExitMismatch).ToArray();
        if (mismatches.Length == 0) return null;
        var other = Checks.Where(check => (check.IsVerification || check.ExpectedExitCode != 0)
            && check.Status != VerificationCheckStatus.Passed && !IsConfirmedExitMismatch(check)).ToArray();
        return "The run could not complete: a declared negative test returned a different exit code.\n\n"
            + string.Join("\n", mismatches.Take(8).Select(check =>
                $"- {check.Command.Replace('\r', ' ').Replace('\n', ' ')}: expected {check.ExpectedExitCode}, observed {check.ObservedExitCode}."))
            + (mismatches.Length > 8 ? $"\n- {mismatches.Length - 8} additional mismatches." : "")
            + (other.Length > 0 ? "\n\nOther outstanding checks: "
                + string.Join(", ", other.Take(8).Select(check => check.Code))
                + (other.Length > 8 ? $"; {other.Length - 8} more" : "") + "." : "")
            + (Overflowed ? "\nRequired verification capacity was exceeded." : "")
            + (_programFailure is not null ? "\nProgram effects are not fully recorded; do not repeat uncertain effects." : "")
            + "\n\nThese checks remain failed. A successful alternative invocation does not change the original result. "
            + "For a native executable's exact exit code, use run_process; a PowerShell wrapper can return 1 instead. "
            + "No automatic retry was scheduled for this final report.";
    }

    public bool HasExitMismatch => Checks.Any(IsConfirmedExitMismatch);

    private static bool IsConfirmedExitMismatch(VerificationCheckEvidence check) => check.ExpectedExitCode != 0
        && check.Status == VerificationCheckStatus.Failed && check.Code == "expected_exit_mismatch"
        && check.ObservedExitCode is not null;

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
            + (pending.Any(check => check.Code == "expected_exit_mismatch")
                ? ". An expected-exit mismatch is an observed result, not a transient tool error. Do not keep rerunning a deliberately failing command to force its expected code. A different invocation does not clear the original check. Report the actual exit and use [BLOCKED] for the incomplete outcome."
                : ". Inspecting logs does not satisfy a check. Rerun the same check after repairs or report [BLOCKED].");
    }

    public static RunVerificationEvidence Project(IEnumerable<ModelContextEntry> originalRunEntries)
    {
        var evidence = new VerificationEvidenceBuffer(MaximumChecks);
        bool hasMutations = false;
        string? programFailure = null;
        foreach (var (entry, proposed) in ExpandProgramEvidence(originalRunEntries, () =>
            { programFailure = "program_evidence_unavailable"; hasMutations = true; evidence.Invalidate(); }))
        {
            var receipt = entry.ToolReceipt!;
            var providerId = receipt.ProviderCallId!;
            bool terminal = proposed.ToolId is "bash" or "powershell" or "run_process" or "terminal_read" or "terminal_stop"
                && proposed.ToolVersion == "1.0.0";
            bool effect = receipt.Outcome == ToolExecutionOutcome.Succeeded || receipt.SideEffectsPossible
                || receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown
                || terminal && receipt.Code is "process_exit_nonzero" or "process_timed_out" or "process_cancelled";
            if (!terminal)
            {
                if (effect && (proposed.ToolId is "write" or "edit" or "write_file" or "edit_file" or "file_delete" or "file_restore" or "apply_patch" or "agent_apply" || receipt.SideEffectsPossible))
                {
                    hasMutations = true;
                    evidence.Invalidate();
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
                foreach (var prior in executionId is null ? [] : evidence.Pending(executionId))
                    evidence.Store(Assess(prior, receipt, entry.Content, providerId, mode == "kill"));
                continue;
            }
            var metadata = ReadTerminalMetadata(entry.Content);
            // A failed lookup is still read-only. Only a consistent, observed normal
            // exit can preserve prior checks; uncertain effects remain conservative.
            var completedRead = metadata.CompletedReadExitCode is { } observedExit
                && (receipt.Outcome == ToolExecutionOutcome.Succeeded && receipt.Code == "ok" && observedExit == 0
                    || receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero" && observedExit != 0);
            if (purpose == "command" && effect && !completedRead)
            {
                hasMutations = true;
                evidence.Invalidate();
            }
            int expectedExit = 0;
            bool negative = purpose == "expected_failure" && TryExpectedExit(args, out expectedExit);
            if (!negative) expectedExit = 0;
            string rawIdentity = CommandIdentity(args, expectedExit);
            string identity = metadata.CanonicalIdentity is { } digest ? "canonical:" + digest + ":" + expectedExit : rawIdentity;
            // Upgrade a legacy check only after the exact original invocation is
            // observed again with authority metadata, never by guessing old paths.
            evidence.UpgradeIdentity(rawIdentity, identity);
            var tracked = evidence.Find(identity);
            bool actualProcessFailure = receipt.Code is "process_exit_nonzero" or "process_timed_out" or "process_cancelled";
            if (tracked is null && purpose != "verification" && !negative && !actualProcessFailure) continue;
            var initial = new VerificationCheckEvidence(identity, providerId, null, expectedExit,
                purpose == "verification" || tracked?.IsVerification == true,
                VerificationCheckStatus.Pending, "verification_pending", DescribeCommand(args));
            var assessed = Assess(initial, receipt, entry.Content, providerId, false);
            evidence.Store(assessed);
            if (negative && assessed.Status == VerificationCheckStatus.Passed
                && receipt.Outcome == ToolExecutionOutcome.Failed)
            {
                evidence.RememberExpectedFailure(ReceiptIdentity(receipt, providerId));
            }
        }
        return new(evidence.Checks, evidence.ExpectedFailures, evidence.Overflowed, hasMutations, programFailure);
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
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out code) && code != 0;
    }
}

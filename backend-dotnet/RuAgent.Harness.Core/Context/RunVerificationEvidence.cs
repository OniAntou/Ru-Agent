using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public enum VerificationCheckStatus { Pending, Passed, Failed, Stale }

public sealed record VerificationCheckEvidence(string Identity, string ProviderCallId,
    string? ExecutionId, int ExpectedExitCode, bool IsVerification,
    VerificationCheckStatus Status, string Code, string Command, bool InvalidatedDuringExecution = false,
    int? ObservedExitCode = null, bool RejectedBeforeLaunch = false,
    DateTimeOffset? ExecutionStartedAtUtc = null, string? RepairIdentity = null, bool InvocationInvalid = false,
    string? WorkingDirectoryIdentity = null, IReadOnlyList<string>? SupersededCheckIdentities = null,
    string? WorkingDirectory = null);

/// <summary>Host-derived original receipt evidence; survives compaction, grants no authority.
/// It also carries the completion evidence (source edits, latest todo list) used by the completion guards.</summary>
public sealed partial class RunVerificationEvidence
{
    public const int MaximumChecks = 256;
    private const string CompletedTodoStatus = "completed";
    public IReadOnlyList<VerificationCheckEvidence> Checks { get; }
    public bool Overflowed { get; }
    public bool HasMutations { get; }

    /// <summary>
    /// True from a successful source edit (or unknown effect) until a declared
    /// purpose=verification check runs. Never set by terminal commands alone.
    /// </summary>
    public bool SourceEditAwaitingVerification { get; }

    /// <summary>
    /// Sticky: true once any source edit ran in this run and no check clears it. The
    /// completion guards use it to tell an edited run from a read-only one.
    /// </summary>
    public bool HasSourceEdits { get; }

    /// <summary>
    /// Rows of the most recent successful todo_write in this run, or null when none ran.
    /// A later empty list is a real, empty list rather than null.
    /// </summary>
    public IReadOnlyList<AgentTodoRow>? LatestTodos { get; }

    /// <summary>Contents of the latest list's rows that are not completed, in list order.</summary>
    public IReadOnlyList<string> OpenTodoItems => LatestTodos is null
        ? Array.Empty<string>()
        : LatestTodos.Where(row => row.Status != CompletedTodoStatus).Select(row => row.Content).ToArray();

    /// <summary>True when the latest list has at least one row and every row is completed.</summary>
    public bool HasCompletedTodoList => LatestTodos is { Count: > 0 } rows
        && rows.All(row => row.Status == CompletedTodoStatus);

    private readonly HashSet<string> _expectedFailures;
    private readonly string? _programFailure;

    private RunVerificationEvidence(IEnumerable<VerificationCheckEvidence> checks,
        HashSet<string> expectedFailures, bool overflowed, bool hasMutations, string? programFailure = null,
        bool sourceEditAwaitingVerification = false, bool hasSourceEdits = false,
        IReadOnlyList<AgentTodoRow>? latestTodos = null)
    {
        Checks = new ReadOnlyCollection<VerificationCheckEvidence>(checks.ToArray());
        _expectedFailures = expectedFailures;
        Overflowed = overflowed;
        HasMutations = hasMutations;
        _programFailure = programFailure;
        SourceEditAwaitingVerification = sourceEditAwaitingVerification;
        HasSourceEdits = hasSourceEdits;
        LatestTodos = latestTodos;
    }

    public string? BlockingCode => _programFailure ?? (Overflowed ? "verification_tracking_limit"
        : OutstandingChecks().FirstOrDefault()?.Code);

    public bool IsExpectedFailure(ToolExecutionReceipt receipt) =>
        receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero"
        && receipt.ProviderCallId is { } id && _expectedFailures.Contains(ReceiptIdentity(receipt, id));

    public static RunVerificationEvidence Project(IEnumerable<ModelContextEntry> originalRunEntries,
        RunVerificationEvidence? previous = null)
    {
        var evidence = new VerificationEvidenceBuffer(MaximumChecks);
        if (previous is not null) evidence.Seed(previous.Checks, previous._expectedFailures, previous.Overflowed);
        bool hasMutations = previous?.HasMutations ?? false;
        bool sourceEditAwaitingVerification = previous?.SourceEditAwaitingVerification ?? false;
        bool hasSourceEdits = previous?.HasSourceEdits ?? false;
        IReadOnlyList<AgentTodoRow>? latestTodos = previous?.LatestTodos;
        string? programFailure = previous?._programFailure;
        foreach (var (entry, proposed) in ExpandProgramEvidence(originalRunEntries, () =>
            {
                programFailure = "program_evidence_unavailable";
                hasMutations = true;
                sourceEditAwaitingVerification = true;
                hasSourceEdits = true;
                evidence.Invalidate();
            }))
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
                // Same gate as the live todo activity: a successful list replaces the earlier one,
                // and a payload that does not parse keeps the earlier list rather than erasing it.
                if (proposed.ToolId == "todo_write" && receipt.Outcome == ToolExecutionOutcome.Succeeded
                    && entry.Content is { } todoContent
                    && AgentTodoPayload.TryRead(Encoding.UTF8.GetBytes(todoContent)) is { } todoRows)
                {
                    latestTodos = todoRows;
                }
                if (effect && (proposed.ToolId is "write" or "edit" or "write_file" or "edit_file" or "file_delete" or "file_restore" or "apply_patch" or "agent_apply" || receipt.SideEffectsPossible))
                {
                    hasMutations = true;
                    evidence.Invalidate();
                }
                if (effect && IsSourceEditCall(proposed))
                {
                    sourceEditAwaitingVerification = true;
                    hasSourceEdits = true;
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
            // A launch rejected before any process started (malformed arguments,
            // policy or resolution failure) observed nothing. Its declared intent
            // stays outstanding, but the identical rejected call can never pass,
            // so a later passing check of the same kind resolves it. It never
            // replaces evidence from an invocation that actually ran.
            bool rejectedBeforeLaunch = receipt.Outcome == ToolExecutionOutcome.Failed
                && !receipt.SideEffectsPossible && !actualProcessFailure;
            if (rejectedBeforeLaunch && tracked is { RejectedBeforeLaunch: false }) continue;
            var initial = new VerificationCheckEvidence(identity, providerId, null, expectedExit,
                purpose == "verification" || tracked?.IsVerification == true,
                VerificationCheckStatus.Pending, "verification_pending", DescribeCommand(args),
                ExecutionStartedAtUtc: entry.ExecutionStartedAtUtc, RepairIdentity: metadata.RepairIdentity,
                InvocationInvalid: expectedExit == 0 && purpose == "verification" && metadata.InvocationInvalid
                    && receipt.Outcome == ToolExecutionOutcome.Failed && receipt.Code == "process_exit_nonzero",
                WorkingDirectoryIdentity: metadata.WorkingDirectoryIdentity, WorkingDirectory: metadata.WorkingDirectory,
                SupersededCheckIdentities: SelectSupersededChecks(args, metadata, evidence, identity) ?? tracked?.SupersededCheckIdentities);
            var assessed = Assess(initial, receipt, entry.Content, providerId, false);
            if (rejectedBeforeLaunch) assessed = assessed with { RejectedBeforeLaunch = true };
            evidence.Store(assessed);
            // A declared check that actually launched answers the awaiting edit, whatever its exit.
            // A failing one stays outstanding through the check path, so this flag never hides it.
            if (purpose == "verification" && !rejectedBeforeLaunch) sourceEditAwaitingVerification = false;
            if (assessed.Status == VerificationCheckStatus.Passed) evidence.ResolveRejectedBy(assessed);
            if (negative && assessed.Status == VerificationCheckStatus.Passed
                && receipt.Outcome == ToolExecutionOutcome.Failed)
            {
                evidence.RememberExpectedFailure(ReceiptIdentity(receipt, providerId));
            }
        }
        return new(evidence.Checks, evidence.ExpectedFailures, evidence.Overflowed, hasMutations, programFailure,
            sourceEditAwaitingVerification, hasSourceEdits, latestTodos);
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
                yield return (ModelContextEntry.ToolResult(nested.Call.ProviderCallId, entry.Sequence, nestedReceipt,
                    nested.Output.GetRawText(), entry.ExecutionStartedAtUtc), nested.Call);
            }
        }
    }

    private static string? StringArg(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Source edits are structured writes outside documentation. A missing or unreadable
    // target and apply_patch or agent_apply count as source. File deletion and restoration
    // are never source edits, so cleaning up a scratch file cannot reopen the nudge.
    private static bool IsSourceEditCall(ModelToolCall call)
    {
        if (call.ToolId is "apply_patch" or "agent_apply") return true;
        if (call.ToolId is not ("write" or "edit" or "write_file" or "edit_file")) return false;
        var args = call.Arguments;
        if (args.ValueKind != JsonValueKind.Object) return true;
        // The legacy write adapter carries delete and restore in op; those are file_delete and file_restore.
        if (call.ToolId == "write" && StringArg(args, "op") is "delete" or "restore") return false;
        return StringArg(args, "path") is not { } path || !VerificationObligationPolicy.IsDocumentationPath(path);
    }

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

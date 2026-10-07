using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    private const int MaxInputAnswerLength = 8192;
    private const int MaxInputQuestionLength = 2048;
    private const int MaxInputOptions = 8;
    private const int MaxInputOptionLength = 512;

    // Host-side one-time input ledger for HTTP answers (first-writer wins).
    // Entries are only a retry guard; the durable input.requested event and the
    // run state remain authoritative. Expire old entries so a host that answers
    // many requests cannot retain an unbounded amount of memory until restart.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> HostInputLedger = new(StringComparer.Ordinal);
    private static readonly TimeSpan HostInputLedgerTtl = TimeSpan.FromMinutes(30);

    internal static void ClearHostInputsForTests() => HostInputLedger.Clear();

    private static void PruneHostInputLedger(DateTimeOffset now)
    {
        var cutoff = now - HostInputLedgerTtl;
        foreach (var entry in HostInputLedger)
        {
            if (entry.Value <= cutoff)
            {
                HostInputLedger.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
            }
        }
    }

    public async Task<AgentServiceResult> HandleInputAsync(string runIdRaw, string inputIdRaw, JsonElement? body, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (!IsInputId(inputIdRaw))
        {
            return BoundedError(400, "invalid_input_id", "input_id invalid.");
        }

        string? answer = null;
        if (body.HasValue
            && body.Value.ValueKind == JsonValueKind.Object
            && body.Value.TryGetProperty("answer", out var answerNode)
            && answerNode.ValueKind == JsonValueKind.String)
        {
            answer = answerNode.GetString();
        }

        if (answer is null
            || answer.Length is 0 or > MaxInputAnswerLength
            || answer.Contains('\0'))
        {
            return BoundedError(400, "invalid_answer", $"answer must be 1..{MaxInputAnswerLength} characters.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        // A durable input request is consumable only while the run is still
        // waiting for that exact input. Relying on an in-memory ledger alone
        // would let a replay after a host restart answer an already-used request.
        if (run.State != HarnessRunState.WaitingUser)
        {
            return BoundedError(409, "input_stale", "Input is missing, stale, or already answered.");
        }

        HarnessEvent? pendingInputEvent = null;
        var resolved = false;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(envelope.Event.Kind, "input.requested", StringComparison.Ordinal))
            {
                pendingInputEvent = envelope.Event;
                resolved = false;
            }
            else if (!resolved
                && pendingInputEvent is not null
                && envelope.Event.Sequence > pendingInputEvent.Sequence
                && string.Equals(envelope.Event.Kind, "run.state_changed", StringComparison.Ordinal)
                && string.Equals(envelope.Event.SafeCode, "input_resolved", StringComparison.Ordinal))
            {
                resolved = true;
            }
        }

        if (pendingInputEvent is null || resolved)
        {
            return BoundedError(409, "input_stale", "Input is missing, stale, or already answered.");
        }

        if (pendingInputEvent.PayloadId is not { } requestPayloadId
            || pendingInputEvent.ToolCallId is not { } toolCallId
            || pendingInputEvent.ExecutionId is not { } executionId
            || pendingInputEvent.ModelStepId is not { } modelStepId
            || string.IsNullOrWhiteSpace(pendingInputEvent.ProviderCallId))
        {
            return BoundedError(409, "input_stale", "Input request is incomplete.");
        }

        var request = await ReadInputRequestAsync(runId, requestPayloadId, cancellationToken).ConfigureAwait(false);
        if (request is null
            || !string.Equals(request.InputId, inputIdRaw, StringComparison.Ordinal))
        {
            return BoundedError(409, "input_stale", "Input is missing, stale, or already answered.");
        }

        // One-time consumption: first writer wins. The durable checks above prevent
        // a replay after restart; this ledger only closes the concurrent HTTP race.
        var key = $"{runIdRaw}|{inputIdRaw}";
        var ledgerNow = _time.UtcNow;
        PruneHostInputLedger(ledgerNow);
        if (!HostInputLedger.TryAdd(key, ledgerNow))
        {
            return BoundedError(409, "input_stale", "input already answered");
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveInput);
        if (!transition.Accepted)
        {
            HostInputLedger.TryRemove(key, out _);
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Input resolve rejected.");
        }

        var now = _time.UtcNow;
        var answerBytes = Encoding.UTF8.GetBytes(answer);
        var answerPayloadId = _ids.NewPayloadId();
        var answerPayload = new HarnessPayloadIntent(
            answerPayloadId,
            HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            answerBytes.Length,
            answerBytes);
        var events = new[]
        {
            new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: modelStepId,
                toolCallId: toolCallId,
                providerCallId: pendingInputEvent.ProviderCallId,
                executionId: executionId,
                safeCode: "input_answered"),
            new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId,
                toolCallId: toolCallId,
                providerCallId: pendingInputEvent.ProviderCallId,
                executionId: executionId,
                payloadId: answerPayloadId,
                safeCode: "input_answered",
                toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false),
            new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.RunStateChanged,
                safeCode: "input_resolved"),
        };
        var nextRun = AdvanceRun(
            run,
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Length,
            now);
        var commit = new HarnessCommit(
            runId,
            run.Version,
            nextRun,
            events,
            new[] { answerPayload },
            Array.Empty<ConversationOutboxIntent>());
        HarnessCommitResult commitResult;
        try
        {
            commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HostInputLedger.TryRemove(key, out _);
            throw;
        }

        if (commitResult.Outcome != HarnessCommitOutcome.Committed)
        {
            HostInputLedger.TryRemove(key, out _);
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, new
        {
            input_id = inputIdRaw,
            status = "answered",
            safe_summary = "input answered",
            run_state = MapRunState(commitResult.Run!.Status),
        });
    }

    private async Task<InputRequestRecord?> ReadInputRequestAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await _payloadStore
                .ReadForClientAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
            if (content.Kind != HarnessPayloadKind.UserInputRequest
                || content.Encoding != HarnessPayloadEncoding.Json)
            {
                return null;
            }

            using var document = JsonDocument.Parse(content.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_id", out var inputNode)
                || inputNode.ValueKind != JsonValueKind.String
                || inputNode.GetString() is not { } inputId
                || !IsInputId(inputId)
                || !root.TryGetProperty("question", out var questionNode)
                || questionNode.ValueKind != JsonValueKind.String
                || questionNode.GetString() is not { } question
                || question.Length is 0 or > MaxInputQuestionLength
                || !root.TryGetProperty("options", out var optionsNode)
                || optionsNode.ValueKind != JsonValueKind.Array
                || optionsNode.GetArrayLength() > MaxInputOptions)
            {
                return null;
            }

            foreach (var option in optionsNode.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String
                    || option.GetString() is not { } text
                    || text.Length > MaxInputOptionLength)
                {
                    return null;
                }
            }

            return new InputRequestRecord(inputId, question);
        }
        catch (Exception error) when (error is JsonException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or HarnessStoreException)
        {
            return null;
        }
    }

    private sealed record InputRequestRecord(string InputId, string Question);
}

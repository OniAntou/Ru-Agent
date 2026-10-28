using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    public void ApplyDurable(HarnessSafeEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventType.StartsWith("run.", StringComparison.Ordinal)
            && envelope.Status is "completed" or "failed" or "cancelled" or "interrupted")
            ForgetParkHistory(envelope.RunId);
        var type = envelope.EventType;
        var summary = Escape(envelope.SafeSummary);

        if (type == "run.state_changed")
        {
            if (string.Equals(envelope.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _sawCompletion = true;
                ClearPaused();
            }
            else if (IsParkedStatus(envelope.Status))
            {
                ParkRun(envelope);
            }
            else if (string.Equals(envelope.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                // The run resumed (approval resolved, reconciliation applied): a pause
                // recorded earlier in this turn no longer holds.
                ClearPaused();
            }

            return;
        }

        if (type == "continue.requested")
        {
            if (IsParkedStatus(envelope.Status))
            {
                ParkRun(envelope);
            }
            else if (string.Equals(envelope.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                // The "continued" commit flips the run back to Running: the parked row
                // must clear, and the frame stays silent like every other internal event.
                ClearPaused();
            }

            return;
        }

        if (type == "model.step_completed"
            && envelope.SafeSummary?.EndsWith("_advised", StringComparison.Ordinal) == true)
        {
            // The host suppressed this step's reply: a recovery note replaced it in the
            // model context and the run takes another step. The deltas already painted
            // it, but suppressed prose is audit-only by design — the rows this step
            // streamed leave the screen so the model's re-answer cannot read as the same
            // answer printed twice.
            _surface.RewindAssistantStep();
            DiscardAssistantTail();
            return;
        }

        if (type is "model.step_started" or "model.step_completed"
            or "prompt_receipt" or "model.usage_recorded" or "model.reasoning_recorded"
            or "tool.proposed" or "input.requested" or "context.compacted")
        {
            // Host bookkeeping, not feed rows. input.requested is answered by the approval
            // path (which prints its own notice) and context.compacted is already surfaced
            // by the compaction activity; printing the durable envelope too duplicated both.
            return;
        }

        if (type == "run.note_added")
        {
            // Internal bookkeeping: runtime-context snapshots, compaction checkpoints,
            // recovery notes and subagent inbox/apply bookkeeping all arrive as run notes.
            // They are audit records, not messages for the operator, and rendering them
            // both added noise and broke the feed block (every notice closes it).
            Tui.TuiDebug.Log("event", $"run.note_added: {summary}");
            return;
        }

        // Metadata can arrive between any two deltas. Only a visible boundary
        // may commit the unfinished Markdown line; flushing above the silent
        // event checks split sentences (and code fences) at network timing.
        Flush();

        if (_hasStreamedDelta)
        {
            if (!_atLineStart)
            {
                _surface.Line();
            }
            _surface.Line();
            _hasStreamedDelta = false;
            _atLineStart = true;
        }

        switch (type)
        {
            case "tool.execution_requested":
                _status.SetState(TuiRunState.Tool);
                return;

            case "tool.execution_completed":
                SettleTool(envelope);
                return;

            case "approval.requested":
                _status.SetState(TuiRunState.WaitingApproval);
                Notice("approval needed · " + summary, TuiToken.Warn);
                return;

            case "reconciliation.required":
                ParkRun(envelope);
                return;

            case "reconciliation.resolved":
                ClearPaused();
                Notice("reconciliation · " + summary, TuiToken.Brand2);
                return;

            case "transient.frames_dropped":
                Notice("some steps were skipped · " + summary, TuiToken.Warn);
                return;

            case "run.failed":
                _terminalCode = string.IsNullOrWhiteSpace(summary) ? "run_failed" : summary;
                return;

            default:
                Notice($"[{envelope.Sequence}] {type}: {summary}", TuiToken.Line);
                return;
        }
    }

    private static bool IsParkedStatus(string? status) =>
        string.Equals(status, "waiting_user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "reconciliation_required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one parked-run line: the safe code plus both resume paths. Static so the
    /// session-open adoption prints the exact same copy a live park does instead of a
    /// near-duplicate that only looks right.
    /// </summary>
    public static string FormatParkedNotice(string runId, string? summary = null, bool repeated = false)
    {
        var reason = ParkReasonCode(summary);
        if (repeated)
            return $"run parked again · {reason ?? "reason pending"} · {ParkAction(runId, reason ?? string.Empty, true)}";
        return reason is null
            ? $"run parked · resume with Continue (or 'ru agent continue {runId}') · cancel the run from CLI to abandon"
            : $"run parked · {reason} · {ParkAction(runId, reason)}";
    }

    private static string? ParkReasonCode(string? code) =>
        string.IsNullOrWhiteSpace(code) || code is "waiting_user" or "reconciliation_required"
            ? null : code.Trim();

    private static string ParkAction(string runId, string reason, bool repeated = false) =>
        repeated ? "the run is still blocked after Continue · cancel the run from CLI to abandon"
            : reason == "provider_quota_exhausted"
                ? "restore provider credits or limits before Continue · cancel the run from CLI to abandon"
                : reason is "provider_rate_limited" or "provider_timeout" or "provider_server_error" or "provider_transport_error"
                    ? "provider temporarily unavailable · wait before Continue · cancel the run from CLI to abandon"
                    : $"resume with Continue (or 'ru agent continue {runId}') · cancel the run from CLI to abandon";

    /// <summary>
    /// The one line for a run this session already has in flight: the operator can
    /// watch it, but the CLI never attaches on its own.
    /// </summary>
    public static string FormatAttachNotice(string runId) =>
        $"run running · follow it with 'ru agent attach {runId}'";

    /// <summary>
    /// Records a run parked on the operator: the live row stops spinning, carries the
    /// bounded safe code plus the resume path, and the transcript prints exactly one
    /// notice per park. Redundant frames for the same park (the backend commits both
    /// <c>reconciliation.required</c> and <c>run.state_changed</c>) must not repeat it.
    /// </summary>
    private void ParkRun(HarnessSafeEventEnvelope envelope)
    {
        var reason = ParkReasonCode(Escape(envelope.SafeSummary));
        var newPark = !_paused || _pausedRunId != envelope.RunId;
        _parkReasons.TryGetValue(envelope.RunId, out var previousReason);
        if (newPark) { _lastParkReason = null; _repeatedPark = false; }
        if (reason is not null && (newPark || reason != _lastParkReason))
            _repeatedPark = reason == previousReason;
        _paused = true;
        _pausedRunId = envelope.RunId;
        if (newPark)
        {
            _lastParkReason = reason;
            Notice(reason is null && previousReason is not null
                ? $"run parked again · reason pending · cancel the run from CLI to abandon"
                : FormatParkedNotice(envelope.RunId, reason, _repeatedPark), TuiToken.Warn);
        }
        else if (reason is not null && reason != _lastParkReason)
        {
            _lastParkReason = reason;
            Notice($"reason: {reason} · {ParkAction(envelope.RunId, reason, _repeatedPark)}", TuiToken.Warn);
        }
        if (reason is not null)
        {
            _parkReasons[envelope.RunId] = reason;
            while (_parkReasons.Count > 64) _parkReasons.Remove(_parkReasons.Keys.First());
        }
        _status.SetState(TuiRunState.Paused);
        _status.SetDetail(_lastParkReason is null ? "waiting to continue"
            : _lastParkReason + " · " + ParkAction(envelope.RunId, _lastParkReason, _repeatedPark));
    }

    public void ForgetParkHistory(string runId)
    {
        _parkReasons.Remove(runId);
        if (_pausedRunId == runId) ClearPaused();
    }

    private void ClearPaused()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        _pausedRunId = null;
        _lastParkReason = null;
        _status.SetDetail(null);
        _status.SetState(TuiRunState.Thinking);
    }
}

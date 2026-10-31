using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

public sealed partial class AgentRunController
{
    private readonly AgentRunFeedState _feedState = new();

    /// <summary>
    /// Reads the payload a durable event points at. A fetch failure is bounded
    /// and observational: the caller keeps its fallback for this event instead
    /// of the stream stopping, and only the failure type is traced.
    /// </summary>
    private async Task<string?> FetchPayloadContentAsync(AgentEventModel model)
    {
        CancellationToken fetchToken;
        try
        {
            fetchToken = _subscriptionCts?.Token ?? CancellationToken.None;
        }
        catch (ObjectDisposedException)
        {
            fetchToken = CancellationToken.None;
        }

        try
        {
            var payload = await _client
                .GetPayloadAsync(model.RunId, model.PayloadRef!, fetchToken)
                .ConfigureAwait(false);
            return payload.Content;
        }
        catch (OperationCanceledException)
        {
            // Subscription torn down; the caller keeps its fallback for this event.
            return null;
        }
        catch (Exception exception)
        {
            GuiDiagnosticWriter.AppendTraceLine(
                $"Agent payload fetch failed: {exception.GetType().Name}");
            return null;
        }
    }

    private async Task OnEventAsync(AgentEventModel model, string runId, long ownershipGeneration)
    {
        if (model.IsTransient)
        {
            var frame = new SseFrame(null, model.EventType, model.TransientData ?? string.Empty, true, model.TransientSequence);
            if (AgentUsageEstimateModel.TryParse(frame, out var estimate) && estimate is not null)
                RunOnUi(() =>
                {
                    if (!IsCurrentFeedOwner(runId, ownershipGeneration) || CurrentRun is not { } run
                        || !string.Equals(run.RunId, runId, StringComparison.Ordinal)) return;
                    ContextEstimateReceived?.Invoke(this, new AgentContextEstimateEventArgs(runId, run.SessionId, estimate));
                });
            // Raw transient output is intentionally not part of the durable event
            // collection. AgentActivityModel travels through the separate callback.
            return;
        }

        AgentModelUsage? usage = null;
        if (string.Equals(model.EventType, "model.usage_recorded", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(model.PayloadRef))
        {
            usage = AgentModelUsage.Parse(await FetchPayloadContentAsync(model).ConfigureAwait(false));
        }

        AgentInputRequest.PendingInput? inputRequest = null;
        if (string.Equals(model.EventType, "input.requested", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(model.PayloadRef))
        {
            inputRequest = AgentInputRequest.Parse(await FetchPayloadContentAsync(model).ConfigureAwait(false));
        }

        if (string.Equals(model.EventType, "final_snapshot", StringComparison.Ordinal)
            && IsTerminalStatus(model.Status))
        {
            lock (_gate)
            {
                if (OwnsFeedLocked(runId, ownershipGeneration)) _feedState.TerminalStatus = model.Status;
            }
        }

        RunOnUi(() =>
        {
            lock (_gate)
            {
                if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                {
                    return;
                }
            }

            if (Events.Count > 200)
            {
                Events.RemoveAt(0);
            }

            Events.Add(model);
            if (!model.IsTransient && string.Equals(model.EventType, "context.compacted", StringComparison.Ordinal))
            {
                ContextCompacted?.Invoke(
                    this,
                    new AgentRunContextCompactedEventArgs(model.RunId, model.SessionId, model.Sequence, ownershipGeneration));
            }
            // A pause reason is a machine token, and a bare state token
            // ("waiting_user") is not a reason either. Resolve the shared park copy
            // before the generic summary-to-status assignment, so the line never
            // flickers through the raw code on its way to the sentence.
            var pauseText = model.EventType == "continue.requested"
                && string.Equals(model.Status, "waiting_user", StringComparison.Ordinal)
                    ? AgentParkReasonCopy.Compose(model.SafeSummary)
                    : string.Empty;
            var summaryIsStateToken = model.EventType == "run.state_changed"
                && model.SafeSummary is "waiting_user" or ReconciliationWaitKind;
            if (!string.IsNullOrEmpty(model.SafeSummary)
                && pauseText.Length == 0
                && !summaryIsStateToken
                && !IsApprovalRequestWithPanel(model)
                && !IsInputRequestEvent(model))
            {
                _statusText = model.SafeSummary;
            }
            if (pauseText.Length > 0)
            {
                _statusText = pauseText;
            }

            if (!model.IsTransient
                && model.EventType == "continue.requested")
            {
                // The frame's status decides, exactly like the TUI: the park commit
                // carries waiting_user, while the "continued" commit flips the run
                // back to running. Keying every frame to waiting_user left a live run
                // parked in the client until the monitor's next refresh -- Continue
                // earned a 409, and a session switch detached a run nobody followed.
                if (string.Equals(model.Status, "waiting_user", StringComparison.Ordinal))
                {
                    _waitKind = "waiting_user";
                }
                else if (string.Equals(model.Status, "running", StringComparison.Ordinal))
                {
                    _waitKind = null;
                }
            }

            // The host parks a run in ReconciliationRequired after an ambiguous
            // tool outcome and stops driving it. Surface that as a resumable wait
            // so the user gets a Continue affordance instead of a silent panel.
            if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, ReconciliationWaitKind, StringComparison.Ordinal))
            {
                _waitKind = ReconciliationWaitKind;
                _statusText = AgentParkReasonCopy.Compose(ReconciliationWaitKind) is { Length: > 0 } pausedReconciliation
                    ? pausedReconciliation
                    : "Run paused: the last action was interrupted. Send Continue to resume with a read-only inspection.";
            }

            if (!model.IsTransient
                && model.EventType == "approval.requested"
                && !string.IsNullOrWhiteSpace(model.ApprovalId))
            {
                PendingApprovalId = model.ApprovalId;
                PendingApprovalSummary = model.SafeSummary;
            }
            else if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, "approval_resolved", StringComparison.Ordinal))
            {
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
            }

            if (!model.IsTransient
                && model.EventType == "input.requested")
            {
                if (inputRequest is not null)
                {
                    PendingInputId = inputRequest.InputId;
                    PendingInputQuestion = inputRequest.Question;
                    PendingInputOptions = inputRequest.Options;
                }
                else
                {
                    // The payload could not be read; the run still waits, so say
                    // what it waits on instead of leaving the panel blank.
                    _statusText = "Waiting for input.";
                }
            }
            else if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, "input_resolved", StringComparison.Ordinal))
            {
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
            }

            if (!model.IsTransient)
            {
                // AgentAuditLog is UI-bound through the composition. Keep both the
                // collection mutation and sink write on the same dispatcher as the
                // event list to avoid cross-thread ObservableCollection access.
                _auditSink?.Invoke(model);
                WriteSink(model);
            }

            if (usage is not null)
            {
                UsageReceived?.Invoke(
                    this,
                    new AgentUsageReceivedEventArgs(model.RunId, model.SessionId, usage, model.Sequence));
            }

            RefreshCommands();
        });

        await Task.CompletedTask;
    }

    private Task OnActivityAsync(AgentActivityModel model, string runId, long ownershipGeneration)
    {
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                if (model.Kind == AgentActivityKind.Terminal && !string.IsNullOrWhiteSpace(model.Detail))
                {
                    PendingApprovalCommand = model.Detail;
                }

                ActivityReceived?.Invoke(this, new AgentRunActivityEventArgs(runId, ownershipGeneration, model));
            }
            catch
            {
                // A visual activity subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnSummaryAsync(AgentRunSummaryModel model, string runId, long ownershipGeneration)
    {
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                SummaryReceived?.Invoke(this, new AgentRunSummaryEventArgs(runId, ownershipGeneration, model));
            }
            catch
            {
                // A visual summary subscriber is observational and must not fail the
                // Agent SSE subscription or durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnAssistantDeltaAsync(AgentAssistantDeltaModel model, string runId, long ownershipGeneration)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested || _currentRunId is null)
            {
                if (!_isCancelRequested)
                {
                    _feedState.RecordIngressDrop();
                }
                else
                {
                    _feedState.RecordCancelDrop();
                }

                return Task.CompletedTask;
            }

        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        if (_isCancelRequested)
                        {
                            _feedState.RecordCancelDrop();
                        }
                        else
                        {
                            _feedState.RecordUiGateDrop();
                        }

                        return;
                    }

                    if (!_feedState.TryAcceptAssistant(runId, model.ModelStepId, model.TransientSequence))
                    {
                        _feedState.RecordDuplicateDrop();
                        return;
                    }
                }

                AssistantDeltaReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual delta subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnToolOutputAsync(AgentToolOutputModel model, string runId, long ownershipGeneration)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy || _isCancelRequested || _currentRunId is null)
            {
                return Task.CompletedTask;
            }

        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!OwnsFeedLocked(runId, ownershipGeneration) || !_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (!_feedState.TryAcceptToolOutput(runId, model.ExecutionId, model.TransientSequence))
                    {
                        return;
                    }
                }

                ToolOutputReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual tool-output subscriber is observational and must not
                // fail the Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private void WriteSink(AgentEventModel model)
    {
        if (_sink is null)
        {
            return;
        }

        var code = model.EventType switch
        {
            "approval.requested" => "agent.approval.required",
            "run.failed" or "run.state_changed" when model.Status == "cancelled" => "agent.cancelled",
            _ => "agent.activity",
        };

        try
        {
            _sink.Write(code, new Dictionary<string, object?>
            {
                ["event_type"] = model.EventType,
                ["safe_summary"] = model.SafeSummary,
                ["sequence"] = model.Sequence,
            });
        }
        catch
        {
            // sink allowlist/validation failures must not break the run feed
        }
    }

    /// <summary>
    /// Follows one run's event stream and settles the controller when the stream
    /// ends. Shared by the create path and adoption so a run is followed by identical
    /// logic either way. <paramref name="afterSequence"/> is the durable cursor to
    /// resume from: a created run starts at 0, an adopted one at its LastSequence so
    /// frames the session open already replayed are not delivered twice.
    /// <paramref name="onFrame"/> fires for every inbound frame so the adopted follow
    /// can tell "cancelled before anything arrived" from "cancelled after the run
    /// already materialized".
    /// </summary>
    private async Task FollowRunStreamAsync(
        string runId,
        long afterSequence,
        long ownershipGeneration,
        CancellationToken token,
        Action? onFrame = null)
    {
        await _client.SubscribeEventsAsync(
                runId,
                afterSequence,
                model =>
                {
                    onFrame?.Invoke();
                    return OnEventAsync(model, runId, ownershipGeneration);
                },
                token,
                model =>
                {
                    onFrame?.Invoke();
                    return OnActivityAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnSummaryAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnAssistantDeltaAsync(model, runId, ownershipGeneration);
                },
                model =>
                {
                    onFrame?.Invoke();
                    return OnToolOutputAsync(model, runId, ownershipGeneration);
                })
            .ConfigureAwait(false);
        // The SSE stream ends after the terminal snapshot, but the create response
        // may be stale (or the stream may have disconnected). Refresh the durable
        // run before changing command state so Cancel is never left enabled for a
        // completed run and a disconnect is not reported as success.
        if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
        try
        {
            await RefreshAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested && TryApplyTerminalStreamSnapshot(runId, ownershipGeneration))
        {
            // The SSE stream already delivered the authoritative terminal
            // snapshot. A follow-up GET can race backend shutdown/restart; do
            // not turn a completed reply into a false client-side failure.
        }

        RunOnUi(() =>
        {
            if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
            var terminal = IsTerminalStatus(CurrentRun?.Status);
            if (terminal)
            {
                _currentRunId = null;
                _waitKind = null;
                PendingApprovalId = null;
                PendingApprovalSummary = null;
                PendingApprovalCommand = null;
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
                if (!_isCancelRequested
                    && !string.Equals(CurrentRun?.Status, "failed", StringComparison.Ordinal))
                {
                    _statusText = string.Empty;
                }
            }
            else if (!_isCancelRequested)
            {
                _statusText = _waitKind switch
                {
                    "waiting_user" => "Waiting to continue. Type Continue to resume.",
                    ReconciliationWaitKind => "Run paused: the last action was interrupted. Send Continue to resume with a read-only inspection.",
                    _ => "Event stream ended before run completed.",
                };
            }

            _isBusy = false;
            RefreshCommands();
        });
    }

    /// <summary>
    /// Runs the adopted run's stream in the background. Unlike the create path there
    /// is no caller to surface a fault to, so every failure is settled into the
    /// controller instead of being rethrown; the run reference is kept so Stop stays
    /// available when the host may still own the run.
    /// </summary>
    private async Task FollowAdoptedRunAsync(
        string runId,
        long afterSequence,
        long ownershipGeneration,
        CancellationToken token,
        CancellationTokenSource cts)
    {
        var sawFrame = false;
        try
        {
            await FollowRunStreamAsync(runId, afterSequence, ownershipGeneration, token, () => sawFrame = true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RunOnUi(() =>
            {
                if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
                if (!sawFrame) ReleaseCurrentRun(runId, "Event monitoring stopped.", ownershipGeneration);
                else
                {
                    _statusText = "Event monitoring stopped.";
                    _isBusy = false;
                    RefreshCommands();
                }
            });
        }
        catch (Exception)
        {
            RunOnUi(() =>
            {
                if (!IsCurrentFeedOwner(runId, ownershipGeneration)) return;
                _statusText = "Event stream ended unexpectedly. Use Stop to end the run, then send again.";
                _isBusy = false;
                RefreshCommands();
            });
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private bool TryApplyTerminalStreamSnapshot(string runId, long ownershipGeneration)
    {
        string? status;
        lock (_gate)
        {
            if (!OwnsFeedLocked(runId, ownershipGeneration)) return false;
            status = _feedState.TerminalStatus;
        }

        if (!IsTerminalStatus(status) || string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        RunOnUi(() =>
        {
            if (!IsCurrentFeedOwner(runId, ownershipGeneration)
                || CurrentRun is not { } current)
            {
                return;
            }

            CurrentRun = current with { Status = status! };
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            PendingApprovalCommand = null;
            PendingInputId = null;
            PendingInputQuestion = null;
            PendingInputOptions = Array.Empty<string>();
            RefreshCommands();
        });
        return true;
    }

}

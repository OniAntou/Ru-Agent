using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed partial class AgentRunController
{
    private readonly AgentRunApprovalCoordinator _approvalCoordinator = new();
    private readonly AgentRunInputCoordinator _inputCoordinator = new();
    public string? PendingApprovalId
    {
        get { lock (_gate) return _approvalCoordinator.Id; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetId(value);
            }

            OnPropertyChanged(nameof(PendingApprovalId));
            OnPropertyChanged(nameof(HasPendingApproval));
        }
    }

    public string? PendingApprovalSummary
    {
        get { lock (_gate) return _approvalCoordinator.Summary; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetSummary(value);
            }

            OnPropertyChanged(nameof(PendingApprovalSummary));
        }
    }

    public string? PendingApprovalCommand
    {
        get { lock (_gate) return _approvalCoordinator.Command; }
        private set
        {
            lock (_gate)
            {
                _approvalCoordinator.SetCommand(value);
            }

            OnPropertyChanged(nameof(PendingApprovalCommand));
        }
    }

    public bool HasPendingApproval => PendingApprovalId is not null;

    public string? PendingInputId
    {
        get { lock (_gate) return _inputCoordinator.Id; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetId(value);
            }

            OnPropertyChanged(nameof(PendingInputId));
            OnPropertyChanged(nameof(HasPendingInput));
        }
    }

    public string? PendingInputQuestion
    {
        get { lock (_gate) return _inputCoordinator.Question; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetQuestion(value);
            }

            OnPropertyChanged(nameof(PendingInputQuestion));
        }
    }

    public IReadOnlyList<string> PendingInputOptions
    {
        get { lock (_gate) return _inputCoordinator.Options; }
        private set
        {
            lock (_gate)
            {
                _inputCoordinator.SetOptions(value);
            }

            OnPropertyChanged(nameof(PendingInputOptions));
        }
    }

    public bool HasPendingInput => PendingInputId is not null;

    /// <summary>The Host's remembered approval rules, when the client can reach them.</summary>
    internal IApprovalRulesClient? ApprovalRules => _client as IApprovalRulesClient;

    public async Task TrustAsync(bool approve, CancellationToken cancellationToken = default)
    {
        var run = CurrentRun;
        if (run is null
            || string.IsNullOrWhiteSpace(run.TrustRequestId)
            || string.IsNullOrWhiteSpace(run.DisplayIdentityHash))
        {
            throw new InvalidOperationException("No workspace trust request is pending.");
        }

        RunOnUi(() => _statusText = approve
            ? "Confirming workspace..."
            : "Rejecting workspace...");
        await _client.TrustAsync(
            run.RunId,
            new TrustAgentRunRequest(
                run.TrustRequestId,
                run.DisplayIdentityHash,
                approve ? "approve" : "deny"),
            cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.AllowOnce, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves an external-directory request for the whole chat session. Only
    /// offered when the pending ask reason is external_directory_required; the
    /// server fails anything else closed and the grant dies with the session.
    /// </summary>
    public async Task ApproveSessionDirectoryAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.AllowSessionDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Approves once and remembers the exact intent as a project rule. Offered
    /// for known non-external asks; the server fails external-directory asks
    /// closed for this scope and unknown reasons stay one-time.
    /// </summary>
    public async Task ApproveAlwaysAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.AllowAlways, cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(CancellationToken cancellationToken = default)
    {
        await SubmitApprovalAsync(AgentApprovalOperation.Deny, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the Host-intercepted question the run is waiting on. The answer
    /// is posted to the same resolve surface the approval decision uses; on
    /// success the card is cleared at once, so submitting hides it even before
    /// the resolved frame arrives.
    /// </summary>
    public async Task SubmitInputAsync(string answer, CancellationToken cancellationToken = default)
    {
        var request = CaptureInputRequest();
        await _inputCoordinator.SubmitAsync(
            answer,
            request.RunId,
            request.InputId,
            _client,
            status => RunOnUi(() => _statusText = status),
            IsCurrentInputRequest,
            () => RunOnUi(() =>
            {
                if (!IsCurrentInputRequest(request.RunId!, request.InputId!)) return;
                PendingInputId = null;
                PendingInputQuestion = null;
                PendingInputOptions = Array.Empty<string>();
            }),
            RefreshAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private Task SubmitApprovalAsync(AgentApprovalOperation operation, CancellationToken cancellationToken) =>
        SubmitApprovalAsync(CaptureApprovalRequest(), operation, cancellationToken);

    private Task SubmitApprovalAsync(
        (string? RunId, string? ApprovalId) request,
        AgentApprovalOperation operation,
        CancellationToken cancellationToken) => _approvalCoordinator.SubmitAsync(
            request.RunId,
            request.ApprovalId,
            operation,
            _client,
            status => RunOnUi(() => _statusText = status),
            IsCurrentApprovalRequest,
            RefreshAsync,
            cancellationToken);

    private (string? RunId, string? ApprovalId) CaptureApprovalRequest()
    {
        lock (_gate) return (_currentRun?.RunId, _approvalCoordinator.Id);
    }

    private (string? RunId, string? InputId) CaptureInputRequest()
    {
        lock (_gate) return (_currentRun?.RunId, _inputCoordinator.Id);
    }

    private bool IsCurrentApprovalRequest(string runId, string approvalId)
    {
        lock (_gate)
        {
            return string.Equals(_currentRun?.RunId, runId, StringComparison.Ordinal)
                && string.Equals(_approvalCoordinator.Id, approvalId, StringComparison.Ordinal);
        }
    }

    private bool IsCurrentInputRequest(string runId, string inputId)
    {
        lock (_gate)
        {
            return string.Equals(_currentRun?.RunId, runId, StringComparison.Ordinal)
                && string.Equals(_inputCoordinator.Id, inputId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Re-arms the question card when a session open adopted a waiting_input run:
    /// the input.requested event sits behind the adoption cursor, so the view
    /// replays the run's durable events and hands the pending question back here.
    /// </summary>
    public void RestorePendingInput(string inputId, string? question, IReadOnlyList<string>? options)
    {
        if (string.IsNullOrWhiteSpace(inputId))
        {
            return;
        }

        RunOnUi(() =>
        {
            PendingInputId = inputId;
            PendingInputQuestion = question;
            PendingInputOptions = options ?? Array.Empty<string>();
        });
    }

}

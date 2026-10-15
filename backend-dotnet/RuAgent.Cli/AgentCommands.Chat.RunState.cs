using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private static bool IsTerminalRunStatus(string? status) =>
        status is not null
        && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("interrupted", StringComparison.OrdinalIgnoreCase));

    private static bool IsStateChangingSlashCommand(string line)
    {
        var command = line.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (command is null || !command.StartsWith('/'))
        {
            return false;
        }

        return command.ToLowerInvariant() is not ("/help" or "/sessions" or "/status" or "/attachments");
    }

    private static bool IsParkedRunStatus(string? status) =>
        string.Equals(status, "waiting_user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "reconciliation_required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a durable frame transitions the run into a parked state. Keyed on the
    /// envelope Status (the authoritative run state) rather than the event name, because
    /// the same event types also carry the resume and are silent then.
    /// </summary>
    private static bool TryReadParkedTransition(SseFrame frame, out string? runId)
    {
        runId = null;
        if (frame.IsTransient || frame.Data is null)
        {
            return false;
        }

        if (!frame.Data.Contains("\"continue.requested\"", StringComparison.Ordinal)
            && !frame.Data.Contains("\"run.state_changed\"", StringComparison.Ordinal)
            && !frame.Data.Contains("\"reconciliation.required\"", StringComparison.Ordinal))
        {
            return false;
        }

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.RunId))
        {
            return false;
        }

        var parked = envelope.EventType switch
        {
            "continue.requested" => string.Equals(envelope.Status, "waiting_user", StringComparison.OrdinalIgnoreCase),
            "run.state_changed" or "reconciliation.required" => IsParkedRunStatus(envelope.Status),
            _ => false,
        };
        if (!parked)
        {
            return false;
        }

        runId = envelope.RunId;
        return true;
    }

    /// <summary>
    /// Signals that a --json chat turn stopped following a run that parked itself
    /// on a user decision. The run is left parked server-side (never auto-answered);
    /// the chat exits so the scripted consumer can answer it out of band.
    /// </summary>
    private abstract class RunParkedException : Exception
    {
        protected RunParkedException(string runId, string message)
            : base(message)
        {
            RunId = runId;
        }

        public string RunId { get; }
    }

    /// <summary>
    /// Signals that a --json chat turn stopped following a run that is waiting for an
    /// operator approval. The run is left waiting server-side (never auto-approved or
    /// auto-denied); the chat exits so the scripted consumer can answer it with
    /// <c>ru agent approve</c>.
    /// </summary>
    private sealed class RunWaitingApprovalException : RunParkedException
    {
        public RunWaitingApprovalException(string runId, string approvalId)
            : base(runId, $"run {runId} is waiting for approval {approvalId}")
        {
            ApprovalId = approvalId;
        }

        public string ApprovalId { get; }
    }

    /// <summary>
    /// Signals that a --json chat turn stopped following a run parked on an
    /// <c>ask_user</c> question. The run is left waiting server-side; the chat exits
    /// so the operator answers it with <c>ru agent answer</c>.
    /// </summary>
    private sealed class RunWaitingInputException : RunParkedException
    {
        public RunWaitingInputException(string runId, string inputId)
            : base(runId, $"run {runId} is waiting for input {inputId}")
        {
            InputId = inputId;
        }

        public string InputId { get; }
    }
}

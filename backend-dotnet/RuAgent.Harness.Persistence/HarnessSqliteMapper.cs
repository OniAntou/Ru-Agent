using System.Globalization;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

/// <summary>
/// Shared SQLite row mapping for harness runs. Public so the host layer reuses
/// this instead of keeping a second, drift-prone copy of the column offsets.
/// </summary>
public static class HarnessSqliteMapper
{
    public const string RunColumns = """
        run_id,
        session_id,
        workspace_id,
        state,
        history_wait_kind,
        version,
        last_sequence,
        budget_window,
        model_steps_in_window,
        tool_calls_in_window,
        preset,
        permission_profile_version,
        policy_snapshot_version,
        policy_snapshot_sha256,
        created_at_utc,
        updated_at_utc,
        terminal_at_utc,
        terminal_error_code
        """;

    public static AgentRun ReadRun(SqliteDataReader reader, int startIndex = 0)
    {
        var state = (HarnessRunState)reader.GetInt32(startIndex + 3);
        HistoryWaitKind? historyWaitKind = reader.IsDBNull(startIndex + 4)
            ? null
            : (HistoryWaitKind)reader.GetInt32(startIndex + 4);
        if (!AgentPermissionPresetMapper.TryParseWireValue(
                reader.GetString(startIndex + 10),
                out var preset))
        {
            throw new HarnessStoreException("harness_run_preset_invalid");
        }

        var permissionProfileVersion = reader.GetInt32(startIndex + 11);
        return new AgentRun(
            new RunId(reader.GetString(startIndex)),
            new SessionId(reader.GetString(startIndex + 1)),
            new WorkspaceId(reader.GetString(startIndex + 2)),
            new HarnessRunStatus(state, historyWaitKind),
            reader.GetInt64(startIndex + 5),
            reader.GetInt64(startIndex + 6),
            reader.GetInt32(startIndex + 7),
            reader.GetInt32(startIndex + 8),
            reader.GetInt32(startIndex + 9),
            preset,
            reader.GetInt64(startIndex + 12),
            reader.GetString(startIndex + 13),
            ParseUtc(reader.GetString(startIndex + 14)),
            ParseUtc(reader.GetString(startIndex + 15)),
            reader.IsDBNull(startIndex + 16)
                ? null
                : ParseUtc(reader.GetString(startIndex + 16)),
            reader.IsDBNull(startIndex + 17) ? null : reader.GetString(startIndex + 17),
            permissionProfileVersion);
    }

    internal static void AddRunProjectionParameters(SqliteCommand command, AgentRun run)
    {
        command.Parameters.AddWithValue("$run_id", run.RunId.Value);
        command.Parameters.AddWithValue("$session_id", run.SessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", run.WorkspaceId.Value);
        command.Parameters.AddWithValue("$state", (int)run.State);
        command.Parameters.AddWithValue(
            "$history_wait_kind",
            run.HistoryWaitKind is { } waitKind ? (int)waitKind : DBNull.Value);
        command.Parameters.AddWithValue("$version", run.Version);
        command.Parameters.AddWithValue("$last_sequence", run.LastSequence);
        command.Parameters.AddWithValue("$budget_window", run.BudgetWindow);
        command.Parameters.AddWithValue("$model_steps", run.ModelStepsInWindow);
        command.Parameters.AddWithValue("$tool_calls", run.ToolCallsInWindow);
        command.Parameters.AddWithValue("$preset", AgentPermissionPresetMapper.ToWireValue(run.Preset));
        command.Parameters.AddWithValue(
            "$permission_profile_version",
            run.PermissionProfileVersion);
        command.Parameters.AddWithValue("$policy_version", run.PolicySnapshotVersion);
        command.Parameters.AddWithValue("$policy_hash", run.PolicySnapshotSha256);
        command.Parameters.AddWithValue("$created_at", Utc(run.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", Utc(run.UpdatedAt));
        command.Parameters.AddWithValue(
            "$terminal_at",
            run.TerminalAt is { } terminalAt ? Utc(terminalAt) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$terminal_error",
            run.TerminalErrorCode is { } errorCode ? errorCode : DBNull.Value);
    }

    internal static HarnessEvent ReadEvent(SqliteDataReader reader)
    {
        return new HarnessEvent(
            new EventId(reader.GetString(0)),
            new RunId(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetString(3),
            ParseUtc(reader.GetString(4)),
            Optional(reader, 5, value => new PayloadId(value)),
            Optional(reader, 6, value => new ModelStepId(value)),
            Optional(reader, 7, value => new ToolCallId(value)),
            Optional(reader, 9, value => new ExecutionId(value)),
            Optional(reader, 10, value => new ApprovalId(value)),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : (ToolExecutionOutcome)reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetBoolean(14),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    internal static string EventKind(HarnessEventKind kind) => kind switch
    {
        HarnessEventKind.RunStateChanged => "run.state_changed",
        HarnessEventKind.RunFailed => "run.failed",
        HarnessEventKind.ModelStepStarted => "model.step_started",
        HarnessEventKind.ModelStepCompleted => "model.step_completed",
        HarnessEventKind.ToolProposed => "tool.proposed",
        HarnessEventKind.ToolExecutionRequested => "tool.execution_requested",
        HarnessEventKind.ToolExecutionCompleted => "tool.execution_completed",
        HarnessEventKind.ApprovalRequested => "approval.requested",
        HarnessEventKind.InputRequested => "input.requested",
        HarnessEventKind.ContinueRequested => "continue.requested",
        HarnessEventKind.ReconciliationRequired => "reconciliation.required",
        HarnessEventKind.ReconciliationResolved => "reconciliation.resolved",
        HarnessEventKind.TransientFramesDropped => "transient.frames_dropped",
        HarnessEventKind.ModelPromptComposed => "model.prompt_composed",
        HarnessEventKind.ContextCompacted => "context.compacted",
        HarnessEventKind.ModelUsageRecorded => "model.usage_recorded",
        HarnessEventKind.RunNoteAdded => "run.note_added",
        HarnessEventKind.ModelReasoningRecorded => "model.reasoning_recorded",
        HarnessEventKind.ProviderContinuationRecorded => "model.provider_continuation_recorded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static byte[] ReadBoundedBlob(
        SqliteDataReader reader,
        int ordinal,
        int maximumLength,
        string errorCode,
        int? requiredLength = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (reader.IsDBNull(ordinal))
        {
            throw new HarnessStoreException(errorCode);
        }

        long length;
        try
        {
            length = reader.GetBytes(ordinal, 0, null, 0, 0);
        }
        catch (Exception exception) when (exception is InvalidCastException or SqliteException)
        {
            throw new HarnessStoreException(errorCode, exception);
        }

        if (length < 0
            || length > maximumLength
            || (requiredLength is { } expected && length != expected))
        {
            throw new HarnessStoreException(errorCode);
        }

        var buffer = new byte[checked((int)length)];
        var copied = reader.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
        if (copied != length)
        {
            throw new HarnessStoreException(errorCode);
        }

        return buffer;
    }

    private static T? Optional<T>(
        SqliteDataReader reader,
        int index,
        Func<string, T> factory)
        where T : struct =>
        reader.IsDBNull(index) ? null : factory(reader.GetString(index));
}

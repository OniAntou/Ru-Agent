using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence;

internal static class SessionWorkStateReceiptReducer
{
    public static SessionWorkState MarkUnrepresented(SessionWorkState state, RunId runId, EventId eventId) =>
        new(state.SessionId, state.WorkspaceId, state.WorkspaceIdentitySha256,
            runId, state.Version + 1, eventId.Value, state.UserGoal, state.UserConstraints,
            state.EarlierUserIntent, state.EarlierUserIntentOmitted, true,
            state.Tasks, state.ObservedFiles, state.Verifications, state.Blocker, state.NextAction);

    public static SessionWorkState? Apply(
        SessionWorkState state,
        ModelToolCall call,
        JsonElement? output,
        bool succeeded,
        RunId runId,
        EventId eventId,
        DateTimeOffset recordedAt)
    {
        if (call.ToolId == "run_code")
        {
            if (output is null || !ProgramReceiptTrace.TryRead(output.Value.GetRawText(), call.ProviderCallId, out var trace))
                return MarkUnrepresented(state, runId, eventId);
            var projected = state;
            foreach (var nested in trace)
                projected = Apply(projected, nested.Call, nested.Output, nested.Outcome == ToolExecutionOutcome.Succeeded,
                    runId, eventId, recordedAt) ?? MarkUnrepresented(projected, runId, eventId);
            return new SessionWorkState(projected.SessionId, projected.WorkspaceId, projected.WorkspaceIdentitySha256,
                runId, state.Version + 1, eventId.Value, projected.UserGoal, projected.UserConstraints,
                projected.EarlierUserIntent, projected.EarlierUserIntentOmitted, projected.HasUnrepresentedReceipts,
                projected.Tasks, projected.ObservedFiles, projected.Verifications, projected.Blocker, projected.NextAction);
        }
        var tasks = succeeded && call.ToolId == "todo_write"
            ? ReadTodos(call.Arguments, runId, eventId)
            : null;
        var files = succeeded ? ReadObservedFiles(call, output, runId, eventId, recordedAt) : null;
        var verification = call.ToolId is "bash" or "powershell" or "run_process"
            ? ReadVerification(call.Arguments, output, runId, eventId, recordedAt)
            : null;
        var isRepresented = tasks is not null || files is not null || verification is not null;
        return new SessionWorkState(state.SessionId, state.WorkspaceId, state.WorkspaceIdentitySha256,
            runId, state.Version + 1, eventId.Value, state.UserGoal, state.UserConstraints,
            state.EarlierUserIntent, state.EarlierUserIntentOmitted,
            state.HasUnrepresentedReceipts || !isRepresented,
            tasks ?? state.Tasks,
            files is null ? state.ObservedFiles : state.ObservedFiles.Concat(files).TakeLast(SessionWorkState.MaximumItems).ToArray(),
            verification is null ? state.Verifications : state.Verifications.Append(verification).TakeLast(SessionWorkState.MaximumItems).ToArray(),
            state.Blocker, state.NextAction);
    }

    private static IReadOnlyList<SessionWorkItem>? ReadTodos(JsonElement arguments, RunId runId, EventId eventId)
    {
        if (!arguments.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array
            || todos.GetArrayLength() > SessionWorkState.MaximumItems) return null;
        var items = new List<SessionWorkItem>(todos.GetArrayLength());
        foreach (var todo in todos.EnumerateArray())
        {
            if (!todo.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                || !todo.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return null;
            var itemStatus = status.GetString() switch
            {
                "pending" or "in_progress" => SessionWorkItemStatus.Open,
                "completed" => SessionWorkItemStatus.Completed,
                _ => SessionWorkItemStatus.Unknown,
            };
            if (itemStatus == SessionWorkItemStatus.Unknown || string.IsNullOrWhiteSpace(content.GetString())) return null;
            items.Add(new SessionWorkItem(content.GetString()!, itemStatus, SessionWorkItemSource.Model, runId.Value, eventId.Value));
        }
        return items;
    }

    private static IReadOnlyList<SessionObservedFile>? ReadObservedFiles(
        ModelToolCall call, JsonElement? output, RunId runId, EventId eventId, DateTimeOffset observedAt)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value) return null;
        var hashName = call.ToolId switch
        {
            "read" or "read_file" => "sha256",
            "write_file" or "edit_file" => "after_sha256",
            "file_restore" => "restored_sha256",
            "write" when ReadOptionalString(call.Arguments, "op") == "restore" => "restored_sha256",
            "write" when ReadOptionalString(call.Arguments, "op") is null or "write" => "after_sha256",
            "edit" => "after_sha256",
            _ => null,
        };
        if (call.ToolId is "patch" or "apply_patch") return ReadPatchFiles(value, runId, eventId, observedAt);
        if (hashName is null || !ReadBoundedString(value, "path", 1024, out var path)
            || !ReadHash(value, hashName, out var hash)) return null;
        return [new SessionObservedFile(path, hash, runId.Value, eventId.Value, observedAt)];
    }

    private static IReadOnlyList<SessionObservedFile>? ReadPatchFiles(
        JsonElement output, RunId runId, EventId eventId, DateTimeOffset observedAt)
    {
        if (!output.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() > SessionWorkState.MaximumItems) return null;
        var observations = new List<SessionObservedFile>(files.GetArrayLength());
        foreach (var file in files.EnumerateArray())
        {
            if (!ReadBoundedString(file, "path", 1024, out var path)
                || !ReadHash(file, "after_sha256", out var hash)) return null;
            observations.Add(new SessionObservedFile(path, hash, runId.Value, eventId.Value, observedAt));
        }
        return observations;
    }

    private static bool ReadHash(JsonElement value, string propertyName, out string hash) =>
        ReadBoundedString(value, propertyName, 64, out hash)
        && hash.Length == 64
        && hash.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string? ReadOptionalString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static SessionVerificationRecord? ReadVerification(
        JsonElement arguments, JsonElement? output, RunId runId, EventId eventId, DateTimeOffset recordedAt)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("exit_code", out var exitCode) || !exitCode.TryGetInt32(out var code)) return null;
        if (!ReadBoundedString(arguments, "command", 4096, out var command)
            && !ReadBoundedString(arguments, "script", 4096, out command))
        {
            if (!ReadBoundedString(arguments, "executable", 4096, out command)) return null;
            if (arguments.TryGetProperty("arguments", out var argv)) command += " " + argv.GetRawText();
            if (command.Length > 4096) return null;
        }
        var succeeded = code == 0;
        return new SessionVerificationRecord(command, $"process_exit_code_{code}", succeeded,
            runId.Value, eventId.Value, recordedAt);
    }

    private static bool ReadBoundedString(JsonElement value, string propertyName, int maxCharacters, out string text)
    {
        text = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString() ?? string.Empty;
        return text.Length is > 0 && text.Length <= maxCharacters;
    }
}

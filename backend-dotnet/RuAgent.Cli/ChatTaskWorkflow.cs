using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;
using RuAgent.Protocol;

namespace RuAgent.Cli;

internal sealed record ChatBackgroundTask(string ExecutionId, string State, int? ExitCode, string Command)
{
    internal bool CanStop => State == "running";
    internal string Detail => $"{State} · {ExecutionId}" + (ExitCode is { } code ? $" · exit {code}" : "");
}

/// <summary>One bounded page of a task's retained output, as the Host reports it.</summary>
internal sealed record ChatTaskOutput(
    string ExecutionId,
    string State,
    int? ExitCode,
    int Offset,
    int Limit,
    int TotalChars,
    string Page,
    bool HasBefore,
    bool HasMore)
{
    internal string Header => $"Task {ExecutionId} · {State}" + (ExitCode is { } code ? $" · exit {code}" : "")
        + $" · chars {Offset}–{Offset + Page.Length} of {TotalChars}";
}

/// <summary>Presentation and exact session/run binding; the Host/Broker owns all task control.</summary>
internal sealed class ChatTaskWorkflow(AgentApiClient client)
{
    internal async Task ExecuteAsync(string sessionId, string? preferredRun, string? argument,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose, TextWriter output, bool json, CancellationToken ct)
    {
        var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? kill = null;
        string? read = null;
        int? readOffset = null;
        int? readLimit = null;
        string? explicitRun = null;
        if (parts.Length == 1 && parts[0].StartsWith("run_", StringComparison.Ordinal)) explicitRun = parts[0];
        else if (parts.Length > 0)
        {
            var parsed = AgentActionRouter.Parse("/tasks " + argument);
            if (!parsed.Success || parsed.Command!.Kind != AgentCommandKind.Tasks)
                throw new AgentApiException("invalid_task_arguments", "Use /tasks [run-id], /tasks kill <execution-id> or /tasks read <execution-id> [offset] [limit].");
            kill = parsed.Command.TaskKillId;
            read = parsed.Command.TaskReadId;
            readOffset = parsed.Command.TaskReadOffset;
            readLimit = parsed.Command.TaskReadLimit;
        }

        var run = explicitRun ?? preferredRun;
        if (string.IsNullOrWhiteSpace(run))
            run = (await client.ListSessionRunIdsAsync(sessionId, ct).ConfigureAwait(false)).LastOrDefault();
        if (string.IsNullOrWhiteSpace(run))
        {
            await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { v = 1, type = "tasks", tasks = Array.Empty<object>() })
                : "No task run in this session yet.").ConfigureAwait(false);
            return;
        }

        while (true)
        {
            if (read is not null)
            {
                await ReadAsync(sessionId, run, read, readOffset, readLimit, output, json, ct).ConfigureAwait(false);
                return;
            }

            var rows = await ListAsync(sessionId, run, ct).ConfigureAwait(false);
            if (kill is not null)
            {
                await StopAsync(sessionId, run, kill, rows, choose, output, json, ct).ConfigureAwait(false);
                return;
            }
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "tasks", run_id = run, tasks = rows.Select(row => new
                { execution_id = row.ExecutionId, state = row.State, exit_code = row.ExitCode, command = row.Command }) })).ConfigureAwait(false);
                return;
            }
            if (choose is null)
            {
                await output.WriteLineAsync($"Background tasks · {run}").ConfigureAwait(false);
                foreach (var row in rows) await output.WriteLineAsync($"{row.Detail} · {row.Command}").ConfigureAwait(false);
                await output.WriteLineAsync(rows.Count == 0
                    ? "No background tasks."
                    : "Stop with /tasks kill <execution-id> · read output with /tasks read <execution-id>.").ConfigureAwait(false);
                return;
            }
            var items = rows.Select(row => new ChoiceItem(string.IsNullOrWhiteSpace(row.Command) ? row.ExecutionId : row.Command,
                row.Detail, "task:" + row.ExecutionId)).ToList();
            items.Add(new ChoiceItem("Refresh", $"{rows.Count(row => row.CanStop)} running · {rows.Count} tasks", "refresh"));
            var selected = await choose(new ChoiceList("Background tasks", items,
                footer: "↑↓ select · enter inspect · esc close"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (selected is null) return;
            if (selected == "refresh") continue;
            var task = rows.FirstOrDefault(row => "task:" + row.ExecutionId == selected);
            if (task is null) throw new AgentApiException("task_selection_invalid", "The selected task is unavailable. Refresh the list.");
            if (!task.CanStop)
            {
                await choose(new ChoiceList("Task · " + task.State,
                    [new ChoiceItem(task.Command, task.Detail, "back"), new ChoiceItem("Back", "Return to tasks", "back")]), ct).ConfigureAwait(false);
                continue;
            }
            await StopAsync(sessionId, run, task.ExecutionId, rows, choose, output, json, ct).ConfigureAwait(false);
            // A successful stop acknowledges the request; list again rather than inventing a final state.
        }
    }

    private async Task StopAsync(string session, string run, string id, IReadOnlyList<ChatBackgroundTask> rows,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose, TextWriter output, bool json, CancellationToken ct)
    {
        var task = rows.FirstOrDefault(row => row.ExecutionId == id);
        if (task is null) throw new AgentApiException("unknown_background_task", "Task is not in this session's background list.");
        if (!task.CanStop) throw new AgentApiException("background_task_not_running", "Task is no longer running. Refresh the list.");
        if (choose is not null)
        {
            var decision = await choose(new ChoiceList("Stop background task?",
                [new ChoiceItem("Keep running", task.Command, "keep"), new ChoiceItem("Stop task", task.Detail, "stop")]), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decision != "stop") return;
        }
        var fresh = await ListAsync(session, run, ct).ConfigureAwait(false);
        if (!fresh.Any(row => row.ExecutionId == id && row.CanStop))
            throw new AgentApiException("background_task_not_running", "Task changed while the panel was open. Refresh the list.");
        await ValidateSessionAsync(session, run, ct).ConfigureAwait(false);
        var result = await client.DispatchCommandAsync(run, "/tasks kill " + id, ct).ConfigureAwait(false);
        EnsureSucceeded(result.Status, result.Code);
        if (result.Data is not JsonElement { ValueKind: JsonValueKind.Object } acknowledgement
            || !acknowledgement.TryGetProperty("killed", out var killed) || killed.ValueKind != JsonValueKind.True)
            throw new AgentApiException("task_stop_unconfirmed", "The Host did not acknowledge the stop. Refresh before trying again.");
        await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { v = 1, type = "task_stop", run_id = run, execution_id = id, status = "requested" })
            : "Stop requested · " + id + " · refresh to check its state.").ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ChatBackgroundTask>> ListAsync(string session, string run, CancellationToken ct)
    {
        await ValidateSessionAsync(session, run, ct).ConfigureAwait(false);
        var response = await client.DispatchCommandAsync(run, "/tasks", ct).ConfigureAwait(false);
        EnsureSucceeded(response.Status, response.Code);
        return Project(response.Data);
    }

    /// <summary>
    /// Prints one bounded page of a task's output. The Host owns the paging
    /// window; this adapter only renders what it returned.
    /// </summary>
    private async Task ReadAsync(string session, string run, string executionId, int? offset, int? limit,
        TextWriter output, bool json, CancellationToken ct)
    {
        await ValidateSessionAsync(session, run, ct).ConfigureAwait(false);
        var command = "/tasks read " + executionId
            + (offset is { } start ? " " + start.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
            + (limit is { } count ? " " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        var response = await client.DispatchCommandAsync(run, command, ct).ConfigureAwait(false);
        EnsureSucceeded(response.Status, response.Code, "task_output");
        var page = ProjectPage(response.Data);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                v = 1,
                type = "task_output",
                run_id = run,
                execution_id = page.ExecutionId,
                state = page.State,
                exit_code = page.ExitCode,
                offset = page.Offset,
                limit = page.Limit,
                total_chars = page.TotalChars,
                has_before = page.HasBefore,
                truncated = page.HasMore,
                page = page.Page,
            })).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(page.Header).ConfigureAwait(false);
        if (page.Page.Length > 0)
        {
            await output.WriteLineAsync(page.Page).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("(no retained output)").ConfigureAwait(false);
        }

        if (page.HasBefore)
        {
            await output.WriteLineAsync($"Earlier output: /tasks read {page.ExecutionId} 0").ConfigureAwait(false);
        }

        if (page.HasMore)
        {
            await output.WriteLineAsync($"Later output: /tasks read {page.ExecutionId} {page.Offset + page.Page.Length}").ConfigureAwait(false);
        }
    }

    private async Task ValidateSessionAsync(string session, string run, CancellationToken ct)
    {
        var snapshot = await client.GetRunAsync(run, ct).ConfigureAwait(false);
        if (snapshot.RunId != run || snapshot.SessionId != session)
            throw new AgentApiException("run_session_mismatch", "Task run belongs to another session.");
    }

    private static void EnsureSucceeded(string status, string code, string expectedCode = "tasks")
    {
        if (status != "succeeded") throw new AgentApiException("tasks_failed", ChatDisplayText.Sanitize(code, 128));
        if (code != expectedCode) throw new AgentApiException("tasks_invalid_response", "Unexpected command result.");
    }

    internal static IReadOnlyList<ChatBackgroundTask> Project(object? data)
    {
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() > 128)
            throw new AgentApiException("tasks_invalid_response", "Background task list is unavailable.");
        var rows = new List<ChatBackgroundTask>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in tasks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new AgentApiException("tasks_invalid_response", "Invalid task row.");
            if (item.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.False) continue;
            var id = Read(item, "execution_id");
            if (id.Length == 0 || id.Any(char.IsControl) || !AgentActionRouter.Parse("/tasks kill " + id).Success || !ids.Add(id))
                throw new AgentApiException("tasks_invalid_response", "Invalid or duplicate task identity.");
            int? exit = item.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) ? number : null;
            var state = Read(item, "state");
            if (state is not ("running" or "exited" or "killed" or "expired" or "failed")) state = "unknown";
            rows.Add(new(id, state, exit, ChatDisplayText.Sanitize(Read(item, "command"), 512)));
        }
        return rows.OrderByDescending(row => row.CanStop).ToArray();
    }

    internal static ChatTaskOutput ProjectPage(object? data)
    {
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } root)
            throw new AgentApiException("tasks_invalid_response", "Task output is unavailable.");
        var id = Read(root, "execution_id");
        if (!root.TryGetProperty("page", out var pageValue) || pageValue.ValueKind != JsonValueKind.String
            || id.Length == 0 || id.Any(char.IsControl))
            throw new AgentApiException("tasks_invalid_response", "Task output is unavailable.");
        var page = pageValue.GetString() ?? "";
        var offset = ReadInt(root, "offset") ?? 0;
        var total = ReadInt(root, "total_chars") ?? offset + page.Length;
        var limit = ReadInt(root, "limit") ?? page.Length;
        var state = Read(root, "state");
        if (state is not ("running" or "exited" or "killed" or "expired" or "failed")) state = "unknown";
        return new(
            id,
            state,
            ReadInt(root, "exit_code"),
            Math.Clamp(offset, 0, total),
            Math.Max(0, limit),
            Math.Max(total, page.Length),
            page,
            ReadBool(root, "has_before"),
            ReadBool(root, "truncated"));
    }

    private static int? ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool ReadBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Read(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

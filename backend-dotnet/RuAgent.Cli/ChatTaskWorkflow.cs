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

/// <summary>Presentation and exact session/run binding; the Host/Broker owns all task control.</summary>
internal sealed class ChatTaskWorkflow(AgentApiClient client)
{
    internal async Task ExecuteAsync(string sessionId, string? preferredRun, string? argument,
        Func<ChoiceList, CancellationToken, Task<string?>>? choose, TextWriter output, bool json, CancellationToken ct)
    {
        var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? kill = null;
        string? explicitRun = null;
        if (parts.Length == 1 && parts[0].StartsWith("run_", StringComparison.Ordinal)) explicitRun = parts[0];
        else if (parts.Length > 0)
        {
            var parsed = AgentActionRouter.Parse("/tasks " + argument);
            if (!parsed.Success || parsed.Command!.Kind != AgentCommandKind.Tasks)
                throw new AgentApiException("invalid_task_arguments", "Use /tasks [run-id] or /tasks kill <execution-id>.");
            kill = parsed.Command.TaskKillId;
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
                await output.WriteLineAsync(rows.Count == 0 ? "No background tasks." : "Stop with /tasks kill <execution-id>.").ConfigureAwait(false);
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

    private async Task ValidateSessionAsync(string session, string run, CancellationToken ct)
    {
        var snapshot = await client.GetRunAsync(run, ct).ConfigureAwait(false);
        if (snapshot.RunId != run || snapshot.SessionId != session)
            throw new AgentApiException("run_session_mismatch", "Task run belongs to another session.");
    }

    private static void EnsureSucceeded(string status, string code)
    {
        if (status != "succeeded") throw new AgentApiException("tasks_failed", ChatDisplayText.Sanitize(code, 128));
        if (code != "tasks") throw new AgentApiException("tasks_invalid_response", "Unexpected command result.");
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

    private static string Read(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

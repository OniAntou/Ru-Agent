using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Cli.Subagents;

/// <summary>Human/JSON projection kept outside the interactive chat state machine.</summary>
public static class SubagentPresenter
{
    public static IEnumerable<string> Lines(SubagentListResponse snapshot)
    {
        yield return $"Subagents · {snapshot.RootRunId} · {snapshot.RootStatus}";
        foreach (var agent in snapshot.Agents)
        {
            var attempt = agent.Attempts.LastOrDefault();
            yield return $"  {agent.AgentId} · {agent.Role} · {attempt?.Status ?? "unavailable"} · {attempt?.Model ?? "model unavailable"}";
            if (agent.Task is not null) yield return "    " + agent.Task;
            if (attempt?.Approval is { } approval)
                yield return $"    Approval {approval.ApprovalId} · {approval.ToolId} · child run {attempt.RunId}";
            foreach (var result in agent.Results)
                yield return $"    Result {result.ResultVersion}: {result.Status} · apply {result.ApplyStatus ?? result.UserAction?.Status ?? "unapplied"}"
                    + (result.Acknowledged ? " · reconciled" : "");
        }
        foreach (var preparation in snapshot.Preparations) yield return $"  {preparation.AgentId} · preparing · {preparation.Stage}";
        if (snapshot.UnavailableAttempts > 0) yield return $"  Unavailable attempts: {snapshot.UnavailableAttempts}";
        yield return $"Family tokens: {snapshot.Usage.TotalTokens?.ToString() ?? "unavailable"}" + (snapshot.Usage.IsComplete ? "" : " · incomplete");
    }

    private static string SafeResultText(string text) => new(Tui.TuiText.Strip(text)
        .Where(c => c is '\r' or '\n' or '\t' || !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());

    public static async Task ExecuteAsync(AgentApiClient client, string root, string? arguments,
        TextWriter output, bool json, CancellationToken ct)
    {
        var parts = (arguments ?? "").Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        object result;
        if (parts.Length == 0)
        {
            var snapshot = await client.ListSubagentsAsync(root, ct);
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(snapshot));
            else foreach (var line in Lines(snapshot)) await output.WriteLineAsync(ChatDisplayText.Sanitize(line, 16384));
            return;
        }
        var agent = parts[0];
        var action = parts.Length > 1 ? parts[1] : "result";
        var remainder = parts.Length > 2 ? parts[2] : "";
        switch (action)
        {
            case "result":
                var detail = await client.GetSubagentAsync(root, agent, ct: ct);
                var selected = remainder.Length == 0 ? detail.Agent.Results.LastOrDefault()?.ResultVersion
                    : long.TryParse(remainder, out var requested) ? requested : throw new AgentApiException("invalid_subagent_arguments", "Invalid result version");
                if (selected is null) { result = detail; break; }
                int? next = 0;
                while (next is { } offset)
                {
                    var detailPage = await client.GetSubagentAsync(root, agent, selected, offset, ct: ct);
                    var page = detailPage.Result;
                    if (page is null)
                    {
                        // The host can return a detail without a result page (expired or
                        // already collected); dereferencing it used to throw a null NRE that
                        // escaped the AgentApiException-only handler above.
                        throw new AgentApiException("subagent_result_unavailable", "The child result page is no longer available.");
                    }
                    if (json) await output.WriteLineAsync(JsonSerializer.Serialize(page));
                    else await output.WriteAsync(SafeResultText(page.Text));
                    next = page.NextOffset;
                    if (next <= offset) throw new AgentApiException("invalid_response", "Invalid page cursor");
                }
                if (!json) await output.WriteLineAsync();
                return;
            default: throw new AgentApiException("invalid_subagent_arguments", "Use /subagents [agent result].");
        }
        // Only the "no result version selected" path reaches here with a value: report the
        // agent identity from that detail, never a case-local that no longer exists.
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(result));
            return;
        }

        var summary = (SubagentDetailResponse)result;
        await output.WriteLineAsync(
            $"agent {summary.Agent.AgentId} · {summary.Agent.Role} · {(summary.Agent.Attempts.LastOrDefault()?.Status ?? "no attempt")}");
    }
}

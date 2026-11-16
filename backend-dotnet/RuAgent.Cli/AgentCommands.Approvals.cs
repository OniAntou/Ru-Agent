using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    public async Task<int> StatusAsync(StatusCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _agentClient.GetRunAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                var obj = new
                {
                    v = 1,
                    type = "status",
                    run_id = run.RunId,
                    session_id = run.SessionId,
                    status = run.Status,
                    preset = run.Preset,
                    version = run.Version,
                    last_sequence = run.LastSequence,
                };
                await _out.WriteLineAsync(JsonSerializer.Serialize(obj)).ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"run_id: {run.RunId}").ConfigureAwait(false);
                await _out.WriteLineAsync($"session_id: {run.SessionId}").ConfigureAwait(false);
                await _out.WriteLineAsync($"status: {run.Status}").ConfigureAwait(false);
            }
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> CancelAsync(CancelCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            await _agentClient.CancelAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"cancelled {cmd.RunId}").ConfigureAwait(false);
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }


    public async Task<int> DispatchCommandAsync(
        DispatchCommandCommand cmd,
        CancellationToken cancellationToken)
    {
        var parsed = AgentActionRouter.Parse(cmd.Text);
        if (!parsed.Success || parsed.Command is null)
        {
            var code = parsed.Code ?? "command_invalid";
            await _err.WriteLineAsync($"{code}: {parsed.SafeMessage}").ConfigureAwait(false);
            return 1;
        }

        try
        {
            var result = await _agentClient
                .DispatchCommandAsync(cmd.RunId, cmd.Text, cancellationToken)
                .ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1,
                    type = "command",
                    run_id = result.RunId,
                    status = result.Status,
                    code = result.Code,
                    data = result.Data,
                    run_version = result.RunVersion,
                })).ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"run_id: {result.RunId}").ConfigureAwait(false);
                await _out.WriteLineAsync($"status: {result.Status}").ConfigureAwait(false);
                await _out.WriteLineAsync($"code: {result.Code}").ConfigureAwait(false);
                await _out.WriteLineAsync($"run_version: {result.RunVersion}").ConfigureAwait(false);
                if (parsed.Command.Kind is RuAgent.Protocol.AgentCommandKind.Help or RuAgent.Protocol.AgentCommandKind.Skills or RuAgent.Protocol.AgentCommandKind.Tasks)
                {
                    await _out.WriteLineAsync(JsonSerializer.Serialize(result.Data)).ConfigureAwait(false);
                }
                else if (parsed.Command.Kind == RuAgent.Protocol.AgentCommandKind.Skill)
                {
                    await _out.WriteLineAsync("result: skill activated for this run; --json includes the captured body and version").ConfigureAwait(false);
                }
                else
                {
                    await _out.WriteLineAsync("result: bounded; see --json for structured metadata").ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> TrustAsync(TrustCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            // The challenge must come from the server: fabricating a trust_request_id or
            // display hash would always fail with trust_stale.
            var run = await _agentClient.GetRunAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(run.TrustRequestId) || string.IsNullOrWhiteSpace(run.DisplayIdentityHash))
            {
                await _err.WriteLineAsync("trust_failed: run has no pending trust challenge").ConfigureAwait(false);
                return 1;
            }

            var url = new Uri(_session.BaseUri, $"agent/v1/runs/{cmd.RunId}/trust");
            var payload = new
            {
                decision = cmd.Approve ? "approve" : "deny",
                trust_request_id = run.TrustRequestId,
                display_identity_hash = run.DisplayIdentityHash,
            };
            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.BearerToken);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedBodyAsync(resp.Content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await _err.WriteLineAsync($"trust_failed: {CliErrorText.ShortMessage(body)}").ConfigureAwait(false);
                return 1;
            }
            await _out.WriteLineAsync($"trust {cmd.RunId} {(cmd.Approve ? "approved" : "denied")}").ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"trust_failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> ApproveAsync(ApproveCommand cmd, CancellationToken cancellationToken)
    {
        var scopeStr = cmd.Always ? "always" : "once";

        try
        {
            var result = await _agentClient.ApproveAsync(cmd.RunId, cmd.ApprovalId, scopeStr, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                // A headless orchestrator answers a pending approval by id; --json
                // is an output format here, not an interactive confirmation.
                var obj = new JsonObject
                {
                    ["v"] = 1,
                    ["type"] = "approval",
                    ["run_id"] = cmd.RunId,
                    ["approval_id"] = cmd.ApprovalId,
                    ["decision"] = "approve",
                    ["scope"] = scopeStr,
                    ["authorized"] = true,
                };
                if (result.TryGetProperty("rule_saved", out var ruleSaved))
                {
                    obj["rule_saved"] = ruleSaved.ValueKind == JsonValueKind.True;
                }

                if (result.TryGetProperty("reusable_rule_not_saved", out var notSaved))
                {
                    obj["reusable_rule_not_saved"] = notSaved.ValueKind == JsonValueKind.True;
                }

                if (result.TryGetProperty("matcher_preview", out var previewElement))
                {
                    obj["matcher_preview"] = JsonNode.Parse(previewElement.GetRawText());
                }

                await _out.WriteLineAsync(obj.ToJsonString()).ConfigureAwait(false);
                return 0;
            }

            // Display matcher preview
            if (result.TryGetProperty("matcher_preview", out var preview))
            {
                await _out.WriteLineAsync($"approval {cmd.ApprovalId} authorized scope:{scopeStr}").ConfigureAwait(false);
                await _out.WriteLineAsync($"preview: {preview.GetRawText()}").ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"approval {cmd.ApprovalId} authorized").ConfigureAwait(false);
            }

            if (result.TryGetProperty("rule_saved", out var rs) && rs.ValueKind == JsonValueKind.True)
                await _out.WriteLineAsync("rule_saved: true").ConfigureAwait(false);
            if (result.TryGetProperty("reusable_rule_not_saved", out var rns) && rns.ValueKind == JsonValueKind.True)
                await _out.WriteLineAsync("reusable_rule_not_saved").ConfigureAwait(false);

            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> DenyAsync(DenyCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _agentClient.DenyAsync(cmd.RunId, cmd.ApprovalId, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                var obj = new JsonObject
                {
                    ["v"] = 1,
                    ["type"] = "approval",
                    ["run_id"] = cmd.RunId,
                    ["approval_id"] = cmd.ApprovalId,
                    ["decision"] = "deny",
                    ["authorized"] = false,
                };
                await _out.WriteLineAsync(obj.ToJsonString()).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync($"approval {cmd.ApprovalId} denied").ConfigureAwait(false);
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> AnswerAsync(AnswerCommand cmd, CancellationToken cancellationToken)
    {
        string answer;
        if (cmd.Answer is null)
        {
            await _out.WriteAsync("Answer: ").ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var line = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                await _err.WriteLineAsync("input_cancelled: no answer provided.").ConfigureAwait(false);
                return 1;
            }

            answer = line;
        }
        else
        {
            answer = cmd.Answer;
        }

        try
        {
            var result = await _agentClient.AnswerAsync(cmd.RunId, cmd.InputId, answer, cancellationToken).ConfigureAwait(false);
            var runState = result.TryGetProperty("run_state", out var stateNode) && stateNode.ValueKind == JsonValueKind.String
                ? stateNode.GetString()
                : null;
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1,
                    type = "input_answer",
                    run_id = cmd.RunId,
                    input_id = cmd.InputId,
                    status = result.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String
                        ? statusNode.GetString()
                        : "answered",
                    run_state = runState,
                })).ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"input {cmd.InputId} answered").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(runState))
                {
                    await _out.WriteLineAsync($"run_state: {runState}").ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await BoundedHttpResponseReader.ReadAsync(content, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex) when (ex.ErrorCode is "response_too_large" or "invalid_response")
        {
            throw new AgentApiException(ex.ErrorCode, "Host response exceeds the 8 MiB client limit.");
        }
    }
}

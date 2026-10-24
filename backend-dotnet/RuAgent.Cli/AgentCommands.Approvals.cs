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

    public async Task<int> ContinueAsync(ContinueCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            await _agentClient.ContinueAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"continued {cmd.RunId}").ConfigureAwait(false);
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

    /// <summary>
    /// Reconciliation UX. Without an explicit --outcome the command only fetches the
    /// reconciliation info (intent code + fresh snapshot hash). With an outcome it
    /// applies the decision; a stale hash surfaces reconcile_stale and never mutates.
    /// </summary>
    public async Task<int> ReconcileAsync(ReconcileCommand cmd, CancellationToken cancellationToken)
    {
        string? infoSnapshotHash = null;
        string? intentCode = null;

        if (cmd.Json && cmd.OutcomeToken is not null && cmd.SnapshotHash is null)
        {
            // --json must never prompt: applying without a fetched hash is impossible.
            await _err.WriteLineAsync("interactive_required: --json requires --hash to apply an outcome.").ConfigureAwait(false);
            return 1;
        }

        if (cmd.SnapshotHash is null)
        {
            // Info round-trip: the host answers invalid_snapshot_hash together with the
            // fresh hash and the safe intent code for the awaiting execution.
            var (infoExit, infoHash, infoIntent) = await PostReconcileInfoAsync(
                cmd.RunId,
                cancellationToken).ConfigureAwait(false);
            if (infoExit != 0)
            {
                return infoExit;
            }

            infoSnapshotHash = infoHash;
            intentCode = infoIntent;
        }

        if (cmd.OutcomeToken is null)
        {
            if (infoSnapshotHash is null || intentCode is null)
            {
                await _err.WriteLineAsync("reconcile_unavailable: no pending reconciliation").ConfigureAwait(false);
                return 1;
            }

            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1,
                    type = "reconcile_info",
                    run_id = cmd.RunId,
                    intent_code = intentCode,
                    snapshot_hash = infoSnapshotHash,
                })).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync($"intent: {intentCode}").ConfigureAwait(false);
            await _out.WriteLineAsync($"snapshot: {infoSnapshotHash}").ConfigureAwait(false);
            await _out.WriteAsync("Outcome applied|not-applied|unknown (a/n/u): ").ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var mapped = answer?.Trim().ToLowerInvariant() switch
            {
                "a" or "applied" => "applied",
                "n" or "not-applied" => "not_applied",
                "u" or "unknown" => "unknown_continue",
                _ => null,
            };
            if (mapped is null)
            {
                await _err.WriteLineAsync("cancelled: no outcome chosen.").ConfigureAwait(false);
                return 1;
            }

            return await PostReconcileDecisionAsync(cmd, mapped, infoSnapshotHash, cancellationToken).ConfigureAwait(false);
        }

        // Reaching here means --outcome was set; the exact "json without hash" case already
        // returned above, so this guard used to be dead code.
        return await PostReconcileDecisionAsync(
            cmd,
            cmd.ToWireOutcome(),
            cmd.SnapshotHash ?? infoSnapshotHash,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Exit, string? SnapshotHash, string? IntentCode)> PostReconcileInfoAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { outcome = "applied", snapshot_hash = new string('0', 64) }),
            Encoding.UTF8,
            "application/json");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/reconcile");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if ((response.StatusCode == HttpStatusCode.BadRequest
                && body.Contains("invalid_snapshot_hash", StringComparison.Ordinal))
            || (response.StatusCode == HttpStatusCode.Conflict
                && body.Contains("reconcile_stale", StringComparison.Ordinal)))
        {
            JsonElement doc;
            try
            {
                using var parsed = JsonDocument.Parse(body);
                doc = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                await _err.WriteLineAsync("reconcile_unavailable: malformed host response").ConfigureAwait(false);
                return (1, null, null);
            }

            var hash = doc.TryGetProperty("snapshot_hash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            var intent = doc.TryGetProperty("intent_code", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
            if (hash is not null)
            {
                return (0, hash, intent);
            }
        }

        await _err.WriteLineAsync($"reconcile_failed: {CliErrorText.ShortMessage(body)}").ConfigureAwait(false);
        return (1, null, null);
    }

    private async Task<int> PostReconcileDecisionAsync(
        ReconcileCommand cmd,
        string wireOutcome,
        string? snapshotHash,
        CancellationToken cancellationToken)
    {
        if (snapshotHash is null)
        {
            await _err.WriteLineAsync("interactive_required: snapshot hash required to apply.").ConfigureAwait(false);
            return 1;
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(new { outcome = wireOutcome, snapshot_hash = snapshotHash }),
            Encoding.UTF8,
            "application/json");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{cmd.RunId}/reconcile");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await _err.WriteLineAsync(body.Contains("reconcile_stale", StringComparison.Ordinal)
                ? $"reconcile_stale: workspace changed since inspection; rerun 'agent reconcile' to refresh."
                : $"reconcile_failed: {CliErrorText.ShortMessage(body)}");
            return 1;
        }

        // --json owns stdout: the human confirmation goes to stderr.
        await (cmd.Json ? _err : _out).WriteLineAsync($"reconciled {cmd.RunId}: {CliErrorText.ShortMessage(body)}").ConfigureAwait(false);
        return 0;
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
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot confirm approval").ConfigureAwait(false);
            return 1;
        }

        var scopeStr = cmd.Always ? "always" : "once";

        try
        {
            var result = await _agentClient.ApproveAsync(cmd.RunId, cmd.ApprovalId, scopeStr, cancellationToken).ConfigureAwait(false);
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
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot deny approval").ConfigureAwait(false);
            return 1;
        }

        try
        {
            var result = await _agentClient.DenyAsync(cmd.RunId, cmd.ApprovalId, cancellationToken).ConfigureAwait(false);
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

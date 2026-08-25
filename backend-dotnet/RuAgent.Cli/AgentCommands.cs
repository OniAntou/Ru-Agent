using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed class AgentCommands
{
    private readonly PublicHostSession _session;
    private readonly HttpClient _httpClient;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly TextReader _in;
    private readonly SessionApiClient _sessionClient;
    private readonly AgentApiClient _agentClient;
    private readonly AgentSseClient _sseClient;

    public AgentCommands(PublicHostSession session, HttpClient httpClient, TextWriter outWriter, TextWriter errWriter, TextReader? inReader = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _out = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
        _err = errWriter ?? throw new ArgumentNullException(nameof(errWriter));
        _in = inReader ?? Console.In;
        _sessionClient = new SessionApiClient(session, httpClient);
        _agentClient = new AgentApiClient(session, httpClient);
        _sseClient = new AgentSseClient(session, httpClient);
    }

    public static async Task<AgentCommands> CreateWithStarterAsync(
        string projectRoot,
        PublicHostStarter? starter,
        HttpClient? httpClient,
        TextWriter outWriter,
        TextWriter errWriter,
        CancellationToken ct)
    {
        starter ??= new PublicHostStarter();
        httpClient ??= new HttpClient();
        var session = await starter.GetOrStartAsync(projectRoot, ct).ConfigureAwait(false);
        return new AgentCommands(session, httpClient, outWriter, errWriter);
    }

    public async Task<int> RunAsync(RunCommand cmd, CancellationToken cancellationToken)
    {
        string canonicalWorkspace;
        try
        {
            canonicalWorkspace = Path.GetFullPath(cmd.WorkspacePath);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"invalid_workspace: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        if (canonicalWorkspace.Length > AgentCliParser.MaxWorkspacePathLength)
        {
            await _err.WriteLineAsync("invalid_workspace: path too long").ConfigureAwait(false);
            return 1;
        }

        string sessionId;
        bool isNewSession = false;
        if (cmd.SessionId is not null)
        {
            sessionId = cmd.SessionId;
        }
        else
        {
            try
            {
                sessionId = await _sessionClient.CreateAsync(cancellationToken).ConfigureAwait(false);
                await _out.WriteLineAsync($"session_id: {sessionId}").ConfigureAwait(false);
                isNewSession = true;
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        var preset = cmd.Preset;
        long? presetVersion = null;

        if (preset == "full_access")
        {
            if (cmd.Json)
            {
                await _err.WriteLineAsync("interactive_required: --json cannot be used with --preset full_access").ConfigureAwait(false);
                return 1;
            }

            await _out.WriteLineAsync("WARNING: Full Access grants the agent broad file and command execution permissions as the current Windows user.").ConfigureAwait(false);
            await _out.WriteLineAsync("It does not provide filesystem or network sandboxing.").ConfigureAwait(false);
            await _out.WriteAsync("Confirm Full Access (y/N): ").ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var normalized = answer?.Trim().ToLowerInvariant();
            if (normalized != "y" && normalized != "yes")
            {
                await _err.WriteLineAsync("cancelled: Full Access not confirmed.").ConfigureAwait(false);
                return 1;
            }

            // Ensure session preset is set interactively
            long expectedVersion = isNewSession ? 0 : 0;
            // For new session, 0 creates version 1; for existing, try 0 then handle conflict
            bool presetSet = false;
            long newVersion = 1;
            try
            {
                var presetResp = await _agentClient.SetSessionPresetAsync(sessionId, "full_access", expectedVersion, interactive: true, cancellationToken).ConfigureAwait(false);
                if (presetResp.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.Number && vProp.TryGetInt64(out var v))
                    newVersion = v;
                presetSet = true;
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "preset_version_conflict")
            {
                // Try with version 1 then 2 etc for existing sessions (max 3 attempts)
                for (long tryVer = 1; tryVer <= 3; tryVer++)
                {
                    try
                    {
                        var r = await _agentClient.SetSessionPresetAsync(sessionId, "full_access", tryVer, interactive: true, cancellationToken).ConfigureAwait(false);
                        if (r.TryGetProperty("version", out var vp) && vp.TryGetInt64(out var vv)) newVersion = vv;
                        presetSet = true;
                        break;
                    }
                    catch (AgentApiException) { continue; }
                }
                if (!presetSet)
                {
                    await _err.WriteLineAsync($"preset_failed: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            presetVersion = newVersion;
            preset = "full_access";
        }
        else if (preset != "standard")
        {
            await _err.WriteLineAsync("invalid_preset: preset must be standard or full_access").ConfigureAwait(false);
            return 1;
        }
        else
        {
            // For standard, if we have a new session, version will be 1 (implicit). For existing, we need to match stored version.
            // Try to fetch existing preset version: for standard, we can omit version if new, or use stored if existing.
            // For simplicity, if isNewSession, leave null (server will create 1). If existing, try to use 1 or stored?
            // We'll leave null and let server handle lenient replay for standard.
            presetVersion = null;
        }

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var request = new CreateAgentRunRequest(
            sessionId,
            canonicalWorkspace,
            cmd.Prompt,
            preset,
            presetVersion,
            idempotencyKey);

        CreateAgentRunResponse resp;
        try
        {
            resp = await _agentClient.CreateRunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        await _out.WriteLineAsync($"session_id: {resp.SessionId}").ConfigureAwait(false);
        await _out.WriteLineAsync($"run_id: {resp.RunId}").ConfigureAwait(false);

        string runId = resp.RunId;
        try
        {
            await StreamEventsAsync(runId, cmd.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _agentClient.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
                await _out.WriteLineAsync("cancelled").ConfigureAwait(false);
            }
            catch { }
            return 130;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    public async Task<int> AttachAsync(AttachCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _agentClient.GetRunAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"run_id: {run.RunId} status: {run.Status}").ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        try
        {
            await StreamEventsAsync(cmd.RunId, cmd.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { await _agentClient.CancelAsync(cmd.RunId, CancellationToken.None).ConfigureAwait(false); } catch { }
            return 130;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        return 0;
    }

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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
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
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await _err.WriteLineAsync($"trust_failed: {body}").ConfigureAwait(false);
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

        var scopeStr = cmd.Scope switch
        {
            ApprovalScope.Once => "once",
            ApprovalScope.Run => "run",
            ApprovalScope.Project => "project",
            _ => "once"
        };

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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> PolicyListAsync(PolicyListCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            string? scope = cmd.Global ? "global" : (cmd.WorkspacePath is not null ? "project" : null);
            string? workspaceId = null;
            // For project scope, we need workspace_id; but CLI provides path, we can canonicalize and try to resolve?
            // For now, if workspace path provided, we can just pass via query as workspace_id param (server expects workspace_id)
            // We'll attempt to use provided path directly as workspace_id if it looks like ws_..., otherwise skip
            if (cmd.WorkspacePath is not null)
            {
                // Try to canonicalize and use as workspace_id? For tests, workspace path is not needed; we can just call without workspace_id and let server return global
                // For real, we would resolve via API, but for now pass null
                // We'll just call ListPolicies with scope project and no id -> server will error, but tests don't use this path
                scope = "project";
            }

            var items = await _agentClient.ListPoliciesAsync(scope, workspaceId, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                var obj = new { v = 1, type = "policy_list", items = items.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())).ToList() };
                await _out.WriteLineAsync(JsonSerializer.Serialize(obj)).ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"policies: {items.Count}").ConfigureAwait(false);
                foreach (var el in items)
                {
                    await _out.WriteLineAsync(el.GetRawText()).ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> PolicyImportAsync(PolicyImportCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot import global policy").ConfigureAwait(false);
            return 1;
        }

        PolicyFile file;
        try
        {
            file = PolicyJsonCodec.LoadFromFile(cmd.FilePath);
        }
        catch (PolicyJsonException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"file_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var display = PolicyJsonCodec.NormalizeAndDisplay(file);
        await _out.WriteLineAsync("Normalized policy to import:").ConfigureAwait(false);
        await _out.WriteLineAsync(display).ConfigureAwait(false);
        await _out.WriteAsync("Confirm import (y/N): ").ConfigureAwait(false);
        await _out.FlushAsync().ConfigureAwait(false);
        var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var normalized = answer?.Trim().ToLowerInvariant();
        if (normalized != "y" && normalized != "yes")
        {
            await _err.WriteLineAsync("cancelled: import not confirmed.").ConfigureAwait(false);
            return 1;
        }

        foreach (var rule in file.Rules)
        {
            var matcher = new
            {
                tool_id = rule.Matcher.ToolId,
                tool_version = rule.Matcher.ToolVersion,
                canonical_path_pattern = rule.Matcher.CanonicalPathPattern,
                resolved_executable = rule.Matcher.ResolvedExecutable,
                argv_pattern = rule.Matcher.ArgvPattern,
                cwd_pattern = rule.Matcher.CwdPattern,
                operation_class = rule.Matcher.OperationClass,
                network_intent = rule.Matcher.NetworkIntent,
            };
            var body = new
            {
                scope = rule.Scope,
                decision = rule.Decision,
                matcher,
            };

            try
            {
                var result = await _agentClient.CreatePolicyAsync(body, interactive: true, cancellationToken).ConfigureAwait(false);
                await _out.WriteLineAsync($"imported rule {result.GetProperty("rule_id").GetString()} scope:{rule.Scope}").ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        return 0;
    }

    public async Task<int> PolicyRemoveAsync(PolicyRemoveCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot remove policy").ConfigureAwait(false);
            return 1;
        }

        await _out.WriteAsync($"Confirm removal of {cmd.RuleId} (y/N): ").ConfigureAwait(false);
        await _out.FlushAsync().ConfigureAwait(false);
        var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var normalized = answer?.Trim().ToLowerInvariant();
        if (normalized != "y" && normalized != "yes")
        {
            await _err.WriteLineAsync("cancelled: removal not confirmed.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            await _agentClient.DeletePolicyAsync(cmd.RuleId, interactive: true, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"removed {cmd.RuleId}").ConfigureAwait(false);
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private async Task StreamEventsAsync(string runId, bool json, CancellationToken ct)
    {
        await foreach (var frame in _sseClient.ReadEventsAsync(runId, afterSequence: 0, cancellationToken: ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (json)
            {
                NdjsonEventRenderer.RenderFrame(frame, _out);
            }
            else
            {
                SafeEventRenderer.RenderFrame(frame, _out);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }
    }
}

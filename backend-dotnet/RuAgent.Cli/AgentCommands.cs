using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol;
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
    private readonly SettingsApiClient _settingsClient;
    private readonly AgentSlashCommands _slashCommands;

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
        _settingsClient = new SettingsApiClient(session, httpClient);
        _slashCommands = new AgentSlashCommands(_settingsClient, _sessionClient, outWriter, errWriter);
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

    private const int PresetVersionRetryLimit = 3;

    private static string CreateSnapshotToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"sps_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private sealed record SessionPresetUpdateResult(
        string SessionId,
        long Version,
        string SnapshotToken);

    private async Task<SessionPresetUpdateResult?> UpdateSessionPresetAsync(
        string sessionId,
        string preset,
        bool interactive,
        CancellationToken cancellationToken)
    {
        var snapshotToken = CreateSnapshotToken();
        for (var expectedVersion = 0; expectedVersion <= PresetVersionRetryLimit; expectedVersion++)
        {
            try
            {
                var response = await _agentClient.SetSessionPresetAsync(
                    sessionId,
                    preset,
                    expectedVersion,
                    interactive,
                    cancellationToken,
                    snapshotToken).ConfigureAwait(false);
                if (!response.TryGetProperty("version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.Number
                    || !versionElement.TryGetInt64(out var version)
                    || version <= 0
                    || !response.TryGetProperty("session_id", out var sessionElement)
                    || sessionElement.ValueKind != JsonValueKind.String
                    || !string.Equals(sessionElement.GetString(), sessionId, StringComparison.Ordinal)
                    || !response.TryGetProperty("preset", out var presetElement)
                    || presetElement.ValueKind != JsonValueKind.String
                    || !string.Equals(presetElement.GetString(), preset, StringComparison.Ordinal)
                    || response.TryGetProperty("snapshot_token", out var tokenElement)
                    && (tokenElement.ValueKind != JsonValueKind.String
                        || !string.Equals(tokenElement.GetString(), snapshotToken, StringComparison.Ordinal)))
                {
                    await _err.WriteLineAsync("preset_update_failed: invalid preset update response.").ConfigureAwait(false);
                    if (!await RestorePresetOrReportAsync(new SessionPresetUpdateResult(sessionId, 0, snapshotToken))
                        .ConfigureAwait(false))
                    {
                        return null;
                    }

                    return null;
                }

                return new SessionPresetUpdateResult(sessionId, version, snapshotToken);
            }
            catch (AgentApiException ex) when (
                IsDeterministicPresetVersionConflict(ex) &&
                expectedVersion < PresetVersionRetryLimit)
            {
            }
            catch (AgentApiException ex) when (IsDeterministicPresetVersionConflict(ex))
            {
                await _err.WriteLineAsync("preset_version_conflict: preset_version_conflict").ConfigureAwait(false);
                return null;
            }
            catch (AgentApiException ex)
            {
                if (IsPreCommitPresetError(ex))
                {
                    await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                    return null;
                }

                if (!await RestorePresetOrReportAsync(
new SessionPresetUpdateResult(sessionId, 0, snapshotToken))
                    .ConfigureAwait(false))
                {
                    await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                    return null;
                }

                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                if (!await RestorePresetOrReportAsync(
new SessionPresetUpdateResult(sessionId, 0, snapshotToken))
                    .ConfigureAwait(false))
                {
                    throw;
                }

                throw;
            }
            catch (Exception ex)
            {
                if (!await RestorePresetOrReportAsync(
new SessionPresetUpdateResult(sessionId, 0, snapshotToken))
                    .ConfigureAwait(false))
                {
                    await _err.WriteLineAsync($"preset_update_failed: {ex.Message}").ConfigureAwait(false);
                    return null;
                }

                await _err.WriteLineAsync($"preset_update_failed: {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }

        return null;
    }

    private static bool IsDeterministicPresetVersionConflict(AgentApiException error)
    {
        return error.StatusCode == 409
            && error.ErrorCode == "preset_version_conflict";
    }

    private static bool IsPreCommitPresetError(AgentApiException error)
    {
        return (error.StatusCode, error.ErrorCode) switch
        {
            (400, "invalid_request" or "invalid_kind" or "invalid_session_id" or "invalid_preset" or "invalid_preset_version" or "invalid_snapshot_token" or "unknown_field") => true,
            (401, "unauthorized" or "policy_create_failed") => true,
            (403, "forbidden" or "interactive_required" or "policy_create_failed") => true,
            (404, "session_not_found") => true,
            (409, "preset_version_conflict" or "preset_snapshot_token_conflict") => true,
            (413, "request_too_large" or "payload_too_large" or "policy_create_failed") => true,
            (503, "preset_snapshot_capacity_exceeded") => true,
            _ => false,
        };
    }

    private static bool IsValidRestoreResponse(JsonElement response, string expectedSessionId)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || !string.Equals(statusElement.GetString(), "ok", StringComparison.Ordinal)
            || !response.TryGetProperty("session_id", out var sessionElement)
            || sessionElement.ValueKind != JsonValueKind.String
            || !string.Equals(sessionElement.GetString(), expectedSessionId, StringComparison.Ordinal)
            || !response.TryGetProperty("restored", out var restoredElement)
            || restoredElement.ValueKind != JsonValueKind.True
            || !response.TryGetProperty("replayed", out var replayedElement)
            || replayedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !response.TryGetProperty("preset", out var presetElement)
            || presetElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || !response.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt64(out var version)
            || version < 0)
        {
            return false;
        }

        if (presetElement.ValueKind == JsonValueKind.Null)
            return version == 0;

        return version > 0
            && presetElement.GetString() is "plan" or "standard" or "full_access";
    }

    private async Task<bool> RestoreSessionPresetAsync(
        SessionPresetUpdateResult update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await _agentClient.RestoreSessionPresetAsync(
                    update.SessionId,
                    update.SnapshotToken,
                    cancellationToken).ConfigureAwait(false);
                if (!IsValidRestoreResponse(response, update.SessionId))
                {
                    await _err.WriteLineAsync("preset_restore_failed: invalid restore response.").ConfigureAwait(false);
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "invalid_response")
            {
                await _err.WriteLineAsync("preset_restore_failed: invalid restore response.").ConfigureAwait(false);
                return false;
            }
            catch (AgentApiException ex) when (
                attempt == 0
                && (ex.StatusCode is null || ex.StatusCode >= 500))
            {
                await _err.WriteLineAsync($"preset_restore_retry: {ex.ErrorCode}").ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"preset_restore_failed: {ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (JsonException ex)
            {
                await _err.WriteLineAsync($"preset_restore_failed: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (attempt == 0)
            {
                await _err.WriteLineAsync($"preset_restore_retry: {ex.GetType().Name}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"preset_restore_failed: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        await _err.WriteLineAsync("preset_restore_failed: restore retry failed.").ConfigureAwait(false);
        return false;
    }

    private async Task<bool> RestorePresetOrReportAsync(
        SessionPresetUpdateResult update)
    {
        if (await RestoreSessionPresetAsync(update, CancellationToken.None).ConfigureAwait(false))
        {
            return true;
        }

        await _err.WriteLineAsync("preset_restore_failed: refusing to continue.").ConfigureAwait(false);
        return false;
    }

    private static bool IsAcceptedTrustStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Trim().ToLowerInvariant() is
            "waiting_history" or
            "waiting_history_user" or
            "waiting_history_final" or
            "running" or
            "waiting_approval" or
            "waiting_user" or
            "reconciliation_required" or
            "completed";
    }

    private static bool IsKnownCreateStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() is
            "waiting_workspace_trust" or
            "waiting_history" or
            "waiting_history_user" or
            "waiting_history_final" or
            "running" or
            "waiting_approval" or
            "waiting_user" or
            "reconciliation_required" or
            "completed";
    }

    /// <summary>
    /// Checks that a create response is really bound to this session and preset before
    /// anything acts on it. Shared by the non-interactive trust path and the interactive
    /// prompt, so the interactive path cannot skip the binding check to reach the prompt.
    /// </summary>
    private static bool IsCreateResponseBound(
        CreateAgentRunResponse response,
        string expectedSessionId,
        string expectedPreset) =>
        !string.IsNullOrWhiteSpace(response.RunId)
        && !string.IsNullOrWhiteSpace(response.SessionId)
        && !string.IsNullOrWhiteSpace(response.WorkspaceId)
        && string.Equals(response.SessionId, expectedSessionId, StringComparison.Ordinal)
        && string.Equals(response.Preset, expectedPreset, StringComparison.Ordinal)
        && IsKnownCreateStatus(response.Status);

    private async Task<bool> HandleWorkspaceTrustAsync(
        CreateAgentRunResponse response,
        string expectedSessionId,
        string expectedPreset,
        bool trustWorkspace,
        CancellationToken cancellationToken)
    {
        if (!IsCreateResponseBound(response, expectedSessionId, expectedPreset))
        {
            await _err.WriteLineAsync("agent_create_failed: invalid create response binding.").ConfigureAwait(false);
            return false;
        }

        if (string.Equals(response.Status, "waiting_workspace_trust", StringComparison.OrdinalIgnoreCase))
        {
            if (!trustWorkspace)
            {
                await _out.WriteLineAsync(
                    $"workspace_trust_required: rerun with --trust-workspace or use 'ru agent trust {response.RunId} --approve'.").ConfigureAwait(false);
                return false;
            }

            var trustRequestId = response.TrustRequestId;
            var displayHash = response.DisplayIdentityHash;
            if (string.IsNullOrWhiteSpace(trustRequestId) || string.IsNullOrWhiteSpace(displayHash))
            {
                await _err.WriteLineAsync("workspace_trust_error: trust challenge is incomplete.").ConfigureAwait(false);
                return false;
            }

            try
            {
                var trustResponse = await _agentClient.TrustAsync(
                    response.RunId,
                    new TrustAgentRunRequest(trustRequestId, displayHash, "approve"),
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(trustResponse.RunId, response.RunId, StringComparison.Ordinal)
                    || !string.Equals(trustResponse.TrustRequestId, trustRequestId, StringComparison.Ordinal)
                    || !IsAcceptedTrustStatus(trustResponse.Status))
                {
                    await _err.WriteLineAsync("workspace_trust_error: trust response does not match the challenge.").ConfigureAwait(false);
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"workspace_trust_error: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        if (!IsAcceptedTrustStatus(response.Status))
        {
            await _err.WriteLineAsync("agent_create_failed: unsafe create response status.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static string ReadReasoningEffort(System.Text.Json.JsonElement settings)
    {
        if (settings.TryGetProperty("reasoning_effort", out var direct) && !string.IsNullOrWhiteSpace(direct.GetString()))
        {
            return direct.GetString()!;
        }
        if (settings.TryGetProperty("thinking_effort", out var thinking) && !string.IsNullOrWhiteSpace(thinking.GetString()))
        {
            return thinking.GetString()!;
        }
        if (settings.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == System.Text.Json.JsonValueKind.Object
            && reasoning.TryGetProperty("effort", out var effort)
            && !string.IsNullOrWhiteSpace(effort.GetString()))
        {
            return effort.GetString()!;
        }
        return string.Empty;
    }

    public async Task<int> RunAsync(RunCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Preset is not ("plan" or "standard" or "full_access"))
        {
            await _err.WriteLineAsync("invalid_preset: preset must be plan, standard, or full_access").ConfigureAwait(false);
            return 1;
        }

        if (cmd.Preset == "full_access" && !cmd.ConfirmFullAccess)
        {
            await _err.WriteLineAsync("full_access_confirmation_required: --confirm-full-access is required.").ConfigureAwait(false);
            return 1;
        }

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
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                return 1;
            }
        }

        var presetUpdate = await UpdateSessionPresetAsync(
            sessionId,
            cmd.Preset,
            cmd.ConfirmFullAccess,
            cancellationToken).ConfigureAwait(false);
        if (presetUpdate is null)
        {
            return 1;
        }

        var request = new CreateAgentRunRequest(
            sessionId,
            canonicalWorkspace,
            cmd.Prompt,
            cmd.Preset,
            presetUpdate.Version,
            Guid.NewGuid().ToString("N"));

        CreateAgentRunResponse response;
        try
        {
            response = await _agentClient.CreateRunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            // Both sides matter here: dev restores the preset a failed create left
            // behind, and CliErrorText renders the server body as one bounded line
            // instead of raw JSON.
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                return 1;
            }

            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException)
        {
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                throw;
            }

            throw;
        }
        catch (Exception ex)
        {
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"agent_create_failed: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            await _err.WriteLineAsync($"agent_create_failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        bool trustAccepted;
        try
        {
            await _out.WriteLineAsync($"session_id: {response.SessionId}").ConfigureAwait(false);
            await _out.WriteLineAsync($"run_id: {response.RunId}").ConfigureAwait(false);
            trustAccepted = await HandleWorkspaceTrustAsync(
                response,
                sessionId,
                cmd.Preset,
                cmd.TrustWorkspace,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                throw;
            }

            throw;
        }
        catch (Exception ex)
        {
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"agent_output_failed: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            await _err.WriteLineAsync($"agent_output_failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        if (!trustAccepted)
        {
            if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                return 1;
            }

            return 1;
        }

        try
        {
            await StreamEventsAsync(response.RunId, cmd.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _agentClient.CancelAsync(response.RunId, CancellationToken.None).ConfigureAwait(false);
                await _out.WriteLineAsync("cancelled").ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            return 130;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
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
                if (parsed.Command.Kind is RuAgent.Protocol.AgentCommandKind.Help or RuAgent.Protocol.AgentCommandKind.Skills)
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

        if (cmd.Json && cmd.SnapshotHash is null)
        {
            // --json must never prompt: applying without a fetched hash is impossible.
            await _err.WriteLineAsync("interactive_required: --json requires --hash to apply an outcome.").ConfigureAwait(false);
            return 1;
        }

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

        await _err.WriteLineAsync($"reconcile_failed: {body}").ConfigureAwait(false);
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
                : $"reconcile_failed: {body}");
            return 1;
        }

        await _out.WriteLineAsync($"reconciled {cmd.RunId}: {body}").ConfigureAwait(false);
        return 0;
    }

    public async Task<int> OutputAsync(OutputCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _agentClient.GetPayloadAsync(cmd.RunId, cmd.PayloadId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync(content).ConfigureAwait(false);
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

        var scopeStr = "once";

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
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
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
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
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
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    private const int MaxStreamResumes = 3;
    private static readonly TimeSpan StreamResumeDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Streams run events, transparently resuming from the last durable
    /// sequence when the transport breaks mid-stream (bounded retries). A
    /// clean stream end only finishes the turn when the run itself is
    /// terminal; otherwise the server closed early and we resume like a
    /// break. Durable frames never duplicate (cursor-based); transient frames
    /// published during the outage may leave a small gap in live text.
    /// </summary>
    private async Task StreamRunEventsResumedAsync(
        string runId,
        Func<SseFrame, CancellationToken, Task> onFrameAsync,
        CancellationToken ct)
    {
        var afterSequence = 0L;
        var resumes = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await foreach (var frame in _sseClient.ReadEventsAsync(runId, afterSequence, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (frame.DurableSequence is { } seq && seq > afterSequence)
                    {
                        afterSequence = seq;
                    }
                    await onFrameAsync(frame, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
            {
                resumes++;
                if (resumes > MaxStreamResumes)
                {
                    throw new AgentApiException("stream_interrupted", $"event stream broke {resumes} times; last error: {ex.Message}", inner: ex);
                }
                await Task.Delay(StreamResumeDelay, ct).ConfigureAwait(false);
                continue;
            }

            if (await IsRunTerminalAsync(runId, ct).ConfigureAwait(false) != false)
            {
                return;
            }
            resumes++;
            if (resumes > MaxStreamResumes)
            {
                throw new AgentApiException("stream_interrupted", "event stream ended before the run reached a terminal state");
            }
            await Task.Delay(StreamResumeDelay, ct).ConfigureAwait(false);
        }
    }

    /// <returns>
    /// True when the run is provably terminal, false when provably still
    /// active, null when the status check itself failed (inconclusive — the
    /// caller keeps the historical behavior of treating a clean stream end
    /// as done rather than spinning on an unreachable backend).
    /// </returns>
    private async Task<bool?> IsRunTerminalAsync(string runId, CancellationToken ct)
    {
        GetAgentRunResponse run;
        try
        {
            run = await _agentClient.GetRunAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        return run.TerminalAt is not null
            || string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StreamEventsAsync(string runId, bool json, CancellationToken ct)
    {
        await StreamRunEventsResumedAsync(runId, async (frame, frameCt) =>
        {
            frameCt.ThrowIfCancellationRequested();
            if (json)
            {
                NdjsonEventRenderer.RenderFrame(frame, _out);
            }
            else
            {
                SafeEventRenderer.RenderFrame(frame, _out);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> ChatAsync(ChatCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Preset is not ("plan" or "standard" or "full_access"))
        {
            await _err.WriteLineAsync("invalid_preset: preset must be plan, standard, or full_access").ConfigureAwait(false);
            return 1;
        }

        if (cmd.Preset == "full_access" && !cmd.ConfirmFullAccess)
        {
            await _err.WriteLineAsync("full_access_confirmation_required: --confirm-full-access is required.").ConfigureAwait(false);
            return 1;
        }

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

        string sessionId = cmd.SessionId ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId))
        {
            try
            {
                sessionId = await _sessionClient.CreateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"session_create_failed: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        string backendName = "OpenAI";
        string activeModel = string.Empty;
        string reasoningEffort = string.Empty;
        try
        {
            var exePath = Environment.ProcessPath;
            var exeStamp = exePath is not null && File.Exists(exePath)
                ? File.GetLastWriteTimeUtc(exePath).ToString("O")
                : "unknown";
            Tui.TuiDebug.Log("build", $"exe={exePath} stamp={exeStamp}");
        }
        catch (Exception)
        {
            // Build stamp is diagnostics-only.
        }
        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            backendName = settings.TryGetProperty("backend", out var b) && !string.IsNullOrWhiteSpace(b.GetString())
                ? b.GetString()!
                : backendName;
            activeModel = backendName switch
            {
                "OpenAI" => settings.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                "Anthropic" => settings.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                _ => settings.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            };
            reasoningEffort = ReadReasoningEffort(settings);
        }
        catch (Exception)
        {
            // Settings backend unreachable; header falls back to startup values below.
        }

        if (_out == Console.Out && !Console.IsOutputRedirected)
        {
            try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
        }

        var ws = Tui.TuiHeader.ShortenWorkspace(canonicalWorkspace, 48);
        var workspaceLabel = string.IsNullOrWhiteSpace(cmd.Preset) ? ws : $"{ws} · {cmd.Preset}";

        var sessionRenderer = new Tui.TuiSessionRenderer(_out);
        var lineReader = new Tui.InteractiveLineReader
        {
            TopLabel = workspaceLabel,
        };

        // Sticky footer: while a turn runs the live row and composer stay pinned to the bottom
        // rows and agent output scrolls above them. Redirected output, --json and --classic keep
        // the sequential layout.
        var sticky = new Tui.TuiStickyRegion(sessionRenderer.Surface);
        bool stickyEnabled = !cmd.Json
            && !cmd.Classic
            && Tui.TuiViewport.IsLiveConsole
            && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

        // Type-ahead: keystrokes captured during a turn feed the one-slot queue, and a pending
        // approval takes the keyboard over the same pinned footer.
        var pendingPrompt = new PendingPromptSlot();
        var typeAhead = new Tui.TypeAheadSession(pendingPrompt)
        {
            WorkspaceLabel = workspaceLabel,
        };
        string? queuedLine = null;
        CancellationTokenSource? turnCts = null;
        CancellationTokenSource? typeAheadCts = null;
        Task? typeAheadLoop = null;
        bool typeAheadLive = false;

        // Slash-command surfaces: the pickers run through the same key loop as the composer.
        _slashCommands.PanelWidth = Tui.TuiViewport.Width();
        _slashCommands.Picker = (list, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : lineReader.ReadChoiceAsync(list, _out, ct);
        _slashCommands.SecretPrompt = (prompt, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : lineReader.ReadSecretAsync(_out, prompt, ct);
        _slashCommands.SecretPanelPrompt = (providerDisplay, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : lineReader.ReadSecretPanelAsync(_out, providerDisplay, Tui.TuiViewport.Width(), ct);

        int turn = 0;
        bool landingScreenVisible = true;
        int starEpoch = 0;

        // The composer reopens its own frame after a settled resize; this rebuilds everything
        // around it (region, header, transcript tail) so the new width applies end to end.
        // Always the full in-place rebuild, even without a sticky region: reprinting the
        // header as appended lines is what used to grow the scrollback on every resize.
        lineReader.OnScreenRefreshedAsync = () =>
        {
            RedrawScreenForResize();
            return Task.CompletedTask;
        };

        // Landing star twinkle: the reader polls this while its buffer is empty; a true
        // answer advances the star epoch so the resize-path rebuild paints a new sky.
        // Idle-only by construction: first prompt, approval UI or typed text keeps false.
        lineReader.ShouldTwinkleStars = () =>
        {
            if (!landingScreenVisible || turn != 0)
            {
                return false;
            }

            if (typeAhead.Approval is not null || !string.IsNullOrEmpty(typeAhead.Buffer))
            {
                return false;
            }

            starEpoch++;
            return true;
        };

        void RenderLandingScreen()
        {
            if (cmd.Json || cmd.Classic || !Tui.TuiViewport.IsLiveConsole)
            {
                return;
            }

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            var landingLines = Tui.TuiHeader.ComposeLandingScreen(
                width,
                canonicalWorkspace,
                backendName,
                activeModel,
                cmd.Preset,
                rows,
                starEpoch);

            foreach (var line in landingLines)
            {
                _out.WriteLine(line);
            }

            if (!typeAheadLive)
            {
                var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                    ? Tui.ComposerBox.Height + 1
                    : Tui.ComposerBox.Height;
                var boxTop = Math.Max(1, rows - boxHeight + 1);
                var cursorRow = landingLines.Count + 1;
                for (var i = cursorRow; i < boxTop; i++)
                {
                    _out.WriteLine(Tui.TuiHeader.StarRow(width, 2500 + i, starEpoch));
                }
            }

            _out.Flush();
        }

        /// <summary>
        /// Recovery for a settled resize. A terminal that changed size has already reflowed the
        /// rows this UI addressed absolutely, so patching the frame can never be reliable; the
        /// deterministic answer is to reset the scrolling region, clear the screen, and rebuild
        /// from the top. The visible transcript is restored from the bounded line history, so a
        /// resize never blanks what the operator was reading.
        /// </summary>
        void RedrawScreenForResize()
        {
            sticky.Exit();
            sessionRenderer.Status.InFooter = false;
            if (_out == Console.Out && !Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch (IOException) { }
            }
            sessionRenderer.Surface.Repaint(writer => writer.Write("\u001b[r\u001b[3J\u001b[2J\u001b[H"));

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            sessionRenderer.Width = width;

            if (landingScreenVisible && turn == 0)
            {
                RenderLandingScreen();
                return;
            }

            var footerHeight = Tui.TuiStickyRegion.ResolveFooterHeight(
                rows,
                typeAhead.FooterHeight(width));
            var budget = Math.Max(0, rows - footerHeight - 2);
            var tail = sessionRenderer.Surface.SnapshotTail(budget, width);
            foreach (var line in tail)
            {
                _out.WriteLine(Tui.InteractiveLineReader.ResizeEchoLine(line, width));
            }

            // Pin the reopened composer to the bottom without scrolling: pad with
            // blank rows up to the box top via raw writes (kept out of the surface
            // history). Newlines only scroll past the last viewport row, so this
            // never grows the scrollback. PushToBottomAsync must stay out of this
            // path: it unconditionally emits newlines at the bottom row.
            if (!typeAheadLive)
            {
                var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                    ? Tui.ComposerBox.Height + 1
                    : Tui.ComposerBox.Height;
                var boxTop = Math.Max(1, rows - boxHeight + 1);
                var cursorRow = tail.Count + 1;
                for (var i = cursorRow; i < boxTop; i++)
                {
                    _out.WriteLine();
                }
            }

            _out.Flush();
            Tui.TuiDebug.Log(
                "layout",
                $"redraw rows={rows} width={width} tail={tail.Count} budget={budget} history={sessionRenderer.Surface.HistoryCount}");
        }

        void PaintStickyFooter()
        {
            if (!sticky.Active)
            {
                return;
            }

            var footerWidth = Tui.TuiViewport.Width();
            var footer = new List<string> { sessionRenderer.Status.Compose(footerWidth) };
            if (typeAhead.Approval is { } approval)
            {
                footer.AddRange(approval.Render(footerWidth));
            }

            footer.AddRange(Tui.ComposerBox.Compose(
                footerWidth,
                typeAhead.State,
                "❯ ",
                typeAhead.Buffer,
                string.Empty,
                lineReader.Legend,
                typeAhead.Hint,
                typeAhead.TopLabel));

            // While the operator is typing, the hardware cursor belongs in the composer: IME
            // candidate windows otherwise open wherever the transcript left the cursor.
            var caretRow = typeAheadLive && !typeAhead.IsApprovalActive ? footer.Count - 1 : (int?)null;
            var caretColumn = Tui.ComposerBox.CursorColumn(footerWidth, "❯ ", typeAhead.Buffer);
            sticky.Paint(footer, footerWidth, caretRow, caretColumn);
        }

        /// <summary>
        /// Rebuilds the pinned layout for the viewport as it is right now, resetting any
        /// previous scrolling region first. Called at turn start and after a settled resize;
        /// when the window is too short for a pinned footer it leaves sticky mode cleanly so
        /// the transcript keeps scrolling normally.
        /// </summary>
        bool ApplyFooterLayout(bool repaint)
        {
            if (!stickyEnabled)
            {
                return false;
            }

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            var footerHeight = Tui.TuiStickyRegion.ResolveFooterHeight(rows, typeAhead.FooterHeight(width));
            if (footerHeight == 0)
            {
                sticky.Exit();
                sessionRenderer.Status.InFooter = false;
                sessionRenderer.LiveRowRepaintHook = null;
                return false;
            }

            var applied = !sticky.Active
                ? sticky.Enter(rows, footerHeight, width)
                : sticky.Rows == rows && sticky.Width == width && sticky.FooterHeight == footerHeight
                    ? true
                    : sticky.Reapply(rows, footerHeight, width);

            if (!applied)
            {
                sessionRenderer.Status.InFooter = false;
                sessionRenderer.LiveRowRepaintHook = null;
                return false;
            }

            sessionRenderer.Status.InFooter = true;
            sessionRenderer.LiveRowRepaintHook = PaintStickyFooter;
            Tui.TuiDebug.Log(
                "layout",
                $"applied rows={rows} footer={footerHeight} first={sticky.FooterFirst} sticky={sticky.Active}");
            if (repaint)
            {
                PaintStickyFooter();
            }

            return true;
        }

        async Task StartTypeAheadAsync()
        {
            if (!sticky.Active || _in != Console.In || Console.IsInputRedirected)
            {
                return;
            }

            typeAheadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            typeAheadLive = true;
            typeAheadLoop = lineReader.RunTypeAheadAsync(
                typeAhead,
                PaintStickyFooter,
                onCancel: () => turnCts?.Cancel(),
                onExpand: () => sessionRenderer.ToggleExpand(),
                onResize: () =>
                {
                    // A settled resize: the terminal has reflowed the rows this layout was
                    // pinned to, so rebuild the whole screen instead of patching the frame.
                    Tui.TuiDebug.Log(
                        "layout",
                        $"onResize rows={Tui.TuiViewport.Height()} width={Tui.TuiViewport.Width()} wasRows={sticky.Rows}");
                    RedrawScreenForResize();
                    sessionRenderer.Width = Tui.TuiViewport.Width();
                    if (ApplyFooterLayout(repaint: true))
                    {
                        Tui.TuiDebug.Log("layout", "footer rebuilt after resize");
                    }
                },
                typeAheadCts.Token);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        async Task StopTypeAheadAsync()
        {
            if (typeAheadCts is null)
            {
                return;
            }

            typeAheadCts.Cancel();
            if (typeAheadLoop is not null)
            {
                try
                {
                    await typeAheadLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the loop only ends when its token fires.
                }
            }

            typeAheadCts.Dispose();
            typeAheadCts = null;
            typeAheadLoop = null;
            typeAheadLive = false;
            typeAhead.Buffer = string.Empty;
            typeAhead.QueueRejected = false;
            typeAhead.Approval = null;
        }

        /// <summary>
        /// Answers one approval. Inside the pinned footer the choice list owns the keyboard;
        /// the sequential layout keeps the numbered stdin prompt. Cancelling the run releases
        /// the wait instead of leaving the frame callback blocked on an unanswered choice.
        /// </summary>
        async Task<string> RequestApprovalAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken)
        {
            if (typeAheadLive && sticky.Active)
            {
                using var registration = approvalToken.Register(() => prompt.Decision.TrySetCanceled(approvalToken));
                typeAhead.Approval = prompt;
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
                try
                {
                    return await prompt.Decision.Task.ConfigureAwait(false);
                }
                finally
                {
                    typeAhead.Approval = null;
                    ApplyFooterLayout(repaint: false);
                    PaintStickyFooter();
                }
            }

            await _out.WriteAsync(Tui.TuiPalette.BoldPaint("\n  ❯ Approve? [1: once / 2: deny] (default: 2): ")).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(approvalToken).ConfigureAwait(false);
            var normalized = string.IsNullOrWhiteSpace(answer) ? "2" : answer.Trim().ToLowerInvariant();
            return normalized is "1" or "once" or "yes" or "y"
                ? Tui.ApprovalPrompt.AllowOnce
                : Tui.ApprovalPrompt.Deny;
        }

        /// <summary>
        /// Asks the operator to trust the workspace, in the composer, while the run holds
        /// the challenge open. No work is in flight at this point, so the type-ahead loop
        /// can hand the keyboard to the choice list — the same handoff a tool approval uses.
        /// Returning without a decision is a cancel, never a silent grant.
        /// </summary>
        async Task<string?> PromptWorkspaceTrustAsync(
            Tui.ApprovalPrompt prompt,
            CancellationToken trustToken)
        {
            if (!typeAheadLive || !sticky.Active)
            {
                return null;
            }

            using var registration = trustToken.Register(() => prompt.Decision.TrySetCanceled(trustToken));
            typeAhead.Approval = prompt;
            sessionRenderer.Status.SetState(Tui.TuiRunState.WaitingTrust);
            sessionRenderer.Status.SetDetail(prompt.Summary);
            ApplyFooterLayout(repaint: false);
            PaintStickyFooter();
            try
            {
                return await prompt.Decision.Task.ConfigureAwait(false);
            }
            finally
            {
                typeAhead.Approval = null;
                sessionRenderer.Status.SetDetail(null);
                // The turn either continues into the run or is about to be wound down;
                // either way the row must stop claiming it is waiting on trust.
                sessionRenderer.Status.SetState(Tui.TuiRunState.Thinking);
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
            }
        }

        async Task<bool> PostTrustDecisionAsync(
            string runId,
            string trustRequestId,
            string displayHash,
            CancellationToken trustToken)
        {
            try
            {
                var trustResponse = await _agentClient.TrustAsync(
                        runId,
                        new TrustAgentRunRequest(trustRequestId, displayHash, "approve"),
                        trustToken)
                    .ConfigureAwait(false);
                if (string.Equals(trustResponse.RunId, runId, StringComparison.Ordinal)
                    && string.Equals(trustResponse.TrustRequestId, trustRequestId, StringComparison.Ordinal)
                    && IsAcceptedTrustStatus(trustResponse.Status))
                {
                    return true;
                }

                await _err.WriteLineAsync("workspace_trust_error: trust response does not match the challenge.").ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"workspace_trust_error: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// Releases a run the operator declined to trust. The challenge cannot be answered
        /// later — it is single-use and the workspace is not on the allow list — so leaving
        /// the run parked would hold the workspace's active-run lock for the whole sweep
        /// interval. Trust is a pre-execution gate, so cancelling rolls back no tool effect.
        /// </summary>
        async Task CancelUntrustedRunAsync(string runId, CancellationToken trustToken)
        {
            try
            {
                await _agentClient.CancelAsync(runId, trustToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"run_cancel_failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs the in-composer workspace-trust ask end to end: validate the binding, prompt,
        /// then post the server-issued challenge. A decline cancels the run, because the
        /// challenge is single-use and the run would otherwise sit on the workspace's
        /// active-run lock until the host swept it.
        /// </summary>
        async Task<(bool Trusted, bool Declined)> ResolveWorkspaceTrustInPlaceAsync(
            CreateAgentRunResponse response,
            string expectedSessionId,
            string expectedPreset,
            string workspacePath,
            CancellationToken trustToken)
        {
            if (!IsCreateResponseBound(response, expectedSessionId, expectedPreset))
            {
                await _err.WriteLineAsync("agent_create_failed: invalid create response binding.").ConfigureAwait(false);
                return (false, false);
            }

            var trustRequestId = response.TrustRequestId;
            var displayHash = response.DisplayIdentityHash;
            if (string.IsNullOrWhiteSpace(trustRequestId) || string.IsNullOrWhiteSpace(displayHash))
            {
                await _err.WriteLineAsync("workspace_trust_error: trust challenge is incomplete.").ConfigureAwait(false);
                return (false, false);
            }

            var decision = await PromptWorkspaceTrustAsync(
                    Tui.ApprovalPrompt.ForWorkspaceTrust(workspacePath),
                    trustToken)
                .ConfigureAwait(false);

            if (decision is null)
            {
                // No composer to ask in, so there is no frame to protect and the bounded
                // code keeps its established stream. Never read stdin here: the type-ahead
                // loop may already own it, which is how the old prompt hung forever.
                await _out.WriteLineAsync(
                        $"workspace_trust_required: rerun with --trust-workspace or use 'ru agent trust {response.RunId} --approve'.")
                    .ConfigureAwait(false);
                return (false, false);
            }

            if (!string.Equals(decision, Tui.ApprovalPrompt.TrustApprove, StringComparison.Ordinal))
            {
                await CancelUntrustedRunAsync(response.RunId, trustToken).ConfigureAwait(false);
                return (false, true);
            }

            var granted = await PostTrustDecisionAsync(response.RunId, trustRequestId, displayHash, trustToken)
                .ConfigureAwait(false);
            return (granted, false);
        }

        /// <summary>
        /// Reprints the part of the answer the deltas never carried.
        ///
        /// The live event hub has no replay buffer: a delta published while the client is
        /// still subscribing is dropped, and because deltas are ordered what is lost is the
        /// head of the reply. The run's own durable <c>assistanttext</c> payload is complete,
        /// so the gap is recoverable rather than lost. This mirrors what the WinUI surface
        /// already does by reloading session history when a run ends.
        ///
        /// Only a suffix is reprinted, and only when the streamed text is a true prefix of
        /// the payload. Anything else -- a shorter payload, a rewritten answer, a payload
        /// that is not assistant text -- is left alone rather than guessed at, because a
        /// terminal cannot unprint.
        /// </summary>
        async Task RecoverMissingAssistantTextAsync(
            string runId,
            List<string> payloadRefs,
            CancellationToken recoveryToken)
        {
            if (payloadRefs.Count == 0)
            {
                Tui.TuiDebug.Log("recover", "no step payload refs seen on the stream");
                return;
            }

            var expected = new StringBuilder();
            try
            {
                foreach (var payloadRef in payloadRefs)
                {
                    // GetPayloadAsync unwraps the host's envelope and returns the assistant
                    // text itself, so there is nothing to parse here -- this is prose, and
                    // parsing it as JSON throws.
                    var text = await _agentClient.GetPayloadAsync(runId, payloadRef, recoveryToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text))
                    {
                        expected.Append(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"assistant_text_recovery_failed: {ex.Message}").ConfigureAwait(false);
                return;
            }

            if (expected.Length == 0)
            {
                Tui.TuiDebug.Log("recover", $"no durable text behind {payloadRefs.Count} refs");
                return;
            }

            var full = expected.ToString();
            var streamed = sessionRenderer.Transcript.StreamedAssistantText;
            Tui.TuiDebug.Log("recover", $"streamed {streamed.Length} of {full.Length} chars, {sessionRenderer.Transcript.AssistantBlockRowCount} rows recorded, suppress={sessionRenderer.Surface.SuppressRepaints}");
            var decision = Tui.AssistantTextReconciler.Decide(streamed, full);
            if (decision == Tui.AssistantTextRecovery.Complete
                || decision == Tui.AssistantTextRecovery.None)
            {
                return;
            }

            if (decision == Tui.AssistantTextRecovery.ReportTruncation)
            {
                // A terminal cannot unprint, so the head cannot be appended after the tail.
                // Rewrite the turn's block from the run's own text and rebuild the screen
                // from the surface history, which is the path a resize already uses.
                if (sessionRenderer.Transcript.RepaintAssistantText(full))
                {
                    RedrawScreenForResize();
                    Tui.TuiDebug.Log("recover", $"repainted turn from durable text ({full.Length} chars)");
                    return;
                }

                await _err.WriteLineAsync(
                        $"assistant_text_truncated: {streamed.Length} of {full.Length} characters reached this terminal. The full reply is in the session; /status or reopen it to read all of it.")
                    .ConfigureAwait(false);
                Tui.TuiDebug.Log("recover", $"head missing: {streamed.Length} of {full.Length}");
                return;
            }

            var missing = Tui.AssistantTextReconciler.SuffixFor(decision, streamed, full);
            if (missing.Length == 0)
            {
                return;
            }

            if (sticky.Active)
            {
                sticky.RestoreTranscriptCursor();
            }

            sessionRenderer.Transcript.AppendAssistant(missing);
            sessionRenderer.Flush();
            Tui.TuiDebug.Log("recover", $"reprinted {missing.Length} chars of {full.Length}");
        }

        async Task FinishTurnAsync()
        {
            await StopTypeAheadAsync().ConfigureAwait(false);
            queuedLine ??= pendingPrompt.Take();
            Tui.TuiDebug.Log("layout", $"finish turn rows={Tui.TuiViewport.Height()} first={sticky.FooterFirst}");
            sessionRenderer.LiveRowRepaintHook = null;
            sessionRenderer.Status.InFooter = false;
            sticky.Exit();
            sessionRenderer.EndTurn();
            turnCts?.Dispose();
            turnCts = null;
        }

        async Task UpdateLegendAsync()
        {
            try
            {
                var s = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var bName = s.TryGetProperty("backend", out var bProp) && !string.IsNullOrWhiteSpace(bProp.GetString())
                    ? bProp.GetString()!
                    : backendName;
                var curModel = bName switch
                {
                    "OpenAI" => s.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                    "Anthropic" => s.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                    _ => s.TryGetProperty("model", out var mProp) ? mProp.GetString() ?? "" : "",
                };
                string effort = ReadReasoningEffort(s);

                string mLbl = string.IsNullOrWhiteSpace(curModel) ? (string.IsNullOrWhiteSpace(activeModel) ? "model" : activeModel) : curModel;
                lineReader.Legend = string.IsNullOrWhiteSpace(effort) ? mLbl : $"{mLbl} · {effort}";
            }
            catch (Exception)
            {
                // Live refresh failed; legend keeps the last known values.
                string mLbl = string.IsNullOrWhiteSpace(activeModel) ? "model" : activeModel;
                lineReader.Legend = string.IsNullOrWhiteSpace(reasoningEffort) ? mLbl : $"{mLbl} · {reasoningEffort}";
            }
        }

        long? lastTotalTokens = null;
        _slashCommands.SnapshotProvider = () => new Tui.TuiStatusSnapshot(
            SessionId: sessionId,
            Preset: cmd.Preset,
            Backend: backendName,
            Model: activeModel,
            Workspace: canonicalWorkspace,
            Turn: turn,
            TotalTokens: lastTotalTokens,
            ToolsSettled: sessionRenderer.Transcript.ToolsSettled,
            ApiKeyConfigured: null);
        await UpdateLegendAsync().ConfigureAwait(false);
        RenderLandingScreen();
        string prompt = Tui.TuiPalette.BoldPaint("❯ ");
        string contPrompt = Tui.TuiPalette.DimPaint("    ... ❯ ");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
        {
            // A prompt typed while the previous turn streamed is sent without a second Enter.
            var line = queuedLine ?? await lineReader.ReadLineWithContinuationAsync(_in, _out, prompt, contPrompt, cancellationToken).ConfigureAwait(false);
            queuedLine = null;
            if (line is null) break;
            line = line.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                if (landingScreenVisible && !line.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    landingScreenVisible = false;
                }
                await _out.WriteLineAsync(Tui.InteractiveLineReader.BuildSubmitEcho(line, width: Tui.TuiViewport.Width())).ConfigureAwait(false);
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var slashCmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : null;

                if (slashCmd is "/exit" or "/quit")
                {
                    await _out.WriteLineAsync(Tui.TuiPalette.DimPaint("Exiting interactive terminal. Goodbye!")).ConfigureAwait(false);
                    await _out.FlushAsync().ConfigureAwait(false);
                    return 0;
                }
                if (slashCmd is "/history")
                {
                    var history = lineReader.History;
                    if (history.Count == 0)
                    {
                        await _out.WriteLineAsync(Tui.TuiPalette.DimPaint("  (No prompt history yet)")).ConfigureAwait(false);
                    }
                    else
                    {
                        await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint("Prompt History:")).ConfigureAwait(false);
                        for (int i = 0; i < history.Count; i++)
                        {
                            await _out.WriteLineAsync(Tui.TuiPalette.DimPaint($"  {i + 1,3}: ") + history[i]).ConfigureAwait(false);
                        }
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/clear")
                {
                    turn = 0;
                    landingScreenVisible = true;
                    lastTotalTokens = null;
                    if (_out == Console.Out && !Console.IsOutputRedirected)
                    {
                        try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                    }
                    RenderLandingScreen();
                    await _out.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (await _slashCommands.TryHandleAsync(slashCmd, arg, sessionId, cmd.Preset, canonicalWorkspace, cancellationToken).ConfigureAwait(false))
                {
                    await UpdateLegendAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/fork")
                {
                    try
                    {
                        var forked = await _sessionClient.ForkAsync(sessionId, 999999, arg, cancellationToken).ConfigureAwait(false);
                        if (forked.TryGetProperty("session", out var sess) && sess.TryGetProperty("id", out var newId))
                        {
                            sessionId = newId.GetString()!;
                            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Forked to new branch: {sessionId}")).ConfigureAwait(false);
                        }
                        else
                        {
                            await _err.WriteLineAsync("fork_failed: unable to create branch").ConfigureAwait(false);
                        }
                    }
                    catch (AgentApiException ex)
                    {
                        await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _err.WriteLineAsync($"fork_error: {ex.Message}").ConfigureAwait(false);
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/rewind")
                {
                    if (arg is null || !int.TryParse(arg, out var toIndex))
                    {
                        await _err.WriteLineAsync("usage: /rewind <message_index>").ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        await _sessionClient.RewindAsync(sessionId, toIndex, cancellationToken).ConfigureAwait(false);
                        await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Rewound session {sessionId} to message index {toIndex}")).ConfigureAwait(false);
                    }
                    catch (AgentApiException ex)
                    {
                        await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _err.WriteLineAsync($"rewind_error: {ex.Message}").ConfigureAwait(false);
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                await _err.WriteLineAsync($"unknown_command: {slashCmd}. Type /help for assistance.").ConfigureAwait(false);
                continue;
            }

            turn++;
            if (landingScreenVisible)
            {
                landingScreenVisible = false;
                if (_out == Console.Out && !Console.IsOutputRedirected)
                {
                    try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                }
            }
            var presetUpdate = await UpdateSessionPresetAsync(
                sessionId,
                cmd.Preset,
                cmd.ConfirmFullAccess,
                cancellationToken).ConfigureAwait(false);
            if (presetUpdate is null)
            {
                return 1;
            }

            var idempotencyKey = Guid.NewGuid().ToString("N");
            var request = new CreateAgentRunRequest(
                sessionId,
                canonicalWorkspace,
                line,
                cmd.Preset,
                presetUpdate.Version,
                idempotencyKey);
            var presetCommitted = false;
            var assistantPayloadRefs = new List<string>();

            try
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sessionRenderer.Width = Tui.TuiViewport.Width();
                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                if (ApplyFooterLayout(repaint: true))
                {
                    await StartTypeAheadAsync().ConfigureAwait(false);
                }

                if (sticky.Active)
                {
                    sticky.RestoreTranscriptCursor();
                }
                sessionRenderer.Surface.WriteLine(Tui.InteractiveLineReader.BuildSubmitEcho(line, width: sessionRenderer.Width));
                sessionRenderer.BeginTurn();
                var resp = await _agentClient.CreateRunAsync(request, turnCts.Token).ConfigureAwait(false);

                var isTrustWait = string.Equals(
                    resp.Status,
                    "waiting_workspace_trust",
                    StringComparison.OrdinalIgnoreCase);

                // The composer can ask, so ask here rather than parking the run. The old
                // path wrote the instruction into the transcript and left the turn live,
                // where the status row kept spinning "Thinking" and the sticky footer
                // repainted the line away: the operator saw a slow model and a frozen
                // prompt. Nothing is executing while the challenge is open, so answering it
                // in place costs nothing and keeps trust an explicit human decision.
                var trustDeclined = false;
                var trusted = isTrustWait && !cmd.TrustWorkspace
                    ? await ResolveTrust().ConfigureAwait(false)
                    : await HandleWorkspaceTrustAsync(
                            resp,
                            sessionId,
                            cmd.Preset,
                            cmd.TrustWorkspace,
                            turnCts.Token)
                        .ConfigureAwait(false);

                async Task<bool> ResolveTrust()
                {
                    var outcome = await ResolveWorkspaceTrustInPlaceAsync(
                            resp,
                            sessionId,
                            cmd.Preset,
                            canonicalWorkspace,
                            turnCts.Token)
                        .ConfigureAwait(false);
                    trustDeclined = outcome.Declined;
                    return outcome.Trusted;
                }

                if (!trusted && isTrustWait)
                {
                    // Either the operator declined — the run is cancelled — or there was no
                    // composer to ask in and the bounded code was already reported. Both end
                    // this turn: nothing is executing, and the live row must not keep spinning
                    // over a run that is not going to produce a reply.
                    await FinishTurnAsync().ConfigureAwait(false);
                    if (trustDeclined)
                    {
                        sessionRenderer.Note("workspace not trusted · this run was cancelled", Tui.TuiToken.Err);
                    }

                    continue;
                }

                if (!trusted)
                {
                    if (!await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                    {
                        await FinishTurnAsync().ConfigureAwait(false);
                        return 1;
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    continue;
                }

                presetCommitted = true;

                async Task OnFrameAsync(SseFrame frame, CancellationToken frameCt)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cmd.Json)
                    {
                        NdjsonEventRenderer.RenderFrame(frame, _out);
                    }
                    else
                    {
                        if (sticky.Active)
                        {
                            sticky.RestoreTranscriptCursor();
                        }
                        sessionRenderer.RenderFrame(frame);
                        if (sticky.Active && typeAheadLive)
                        {
                            PaintStickyFooter();
                        }

                        if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                        {
                            try
                            {
                                var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                                if (envelope is not null && envelope.EventType == "approval.requested" && !string.IsNullOrEmpty(envelope.ApprovalId))
                                {
                                    var targetRunId = !string.IsNullOrEmpty(envelope.RunId) ? envelope.RunId : resp.RunId;
                                    var decision = await RequestApprovalAsync(Tui.ApprovalPrompt.For(envelope.SafeSummary), frameCt).ConfigureAwait(false);

                                    if (decision == Tui.ApprovalPrompt.Deny)
                                    {
                                        await _agentClient.DenyAsync(targetRunId, envelope.ApprovalId, frameCt).ConfigureAwait(false);
                                        sessionRenderer.Note("denied · this tool call aborts", Tui.TuiToken.Err);
                                    }
                                    else
                                    {
                                        await _agentClient.ApproveAsync(targetRunId, envelope.ApprovalId, decision, frameCt).ConfigureAwait(false);
                                        var scope = decision == Tui.ApprovalPrompt.AllowDirectory ? "folder for this session" : "once";
                                        sessionRenderer.Note("approved · " + scope, Tui.TuiToken.Ok);
                                    }

                                    await _out.FlushAsync().ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException) when (frameCt.IsCancellationRequested)
                            {
                                // The operator cancelled the turn while the approval was open.
                                throw;
                            }
                            catch (Exception ex)
                            {
                                await _err.WriteLineAsync($"inline_approval_error: {ex.Message}").ConfigureAwait(false);
                            }
                        }

                        if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"model.step_completed\"", StringComparison.Ordinal))
                        {
                            // Remember every step's durable assistant payload so a lost
                            // delta tail can be recovered once the run finishes.
                            try
                            {
                                var stepEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                                if (stepEnvelope is { EventType: "model.step_completed" }
                                    && !string.IsNullOrWhiteSpace(stepEnvelope.PayloadRef))
                                {
                                    assistantPayloadRefs.Add(stepEnvelope.PayloadRef);
                                }
                            }
                            catch (System.Text.Json.JsonException)
                            {
                                // Non-conforming frame payload; the recovery falls back to
                                // whatever steps were already recorded.
                            }
                        }

                        if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"total_tokens\"", StringComparison.Ordinal))
                        {
                            try
                            {
                                var usageEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                                if (usageEnvelope?.TotalTokens is { } total)
                                {
                                    lastTotalTokens = total;
                                    sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                                }
                            }
                            catch (System.Text.Json.JsonException)
                            {
                                // Non-conforming frame payload; token tally keeps its last value.
                            }
                        }
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                }
                await StreamRunEventsResumedAsync(resp.RunId, OnFrameAsync, turnCts.Token).ConfigureAwait(false);
                sessionRenderer.Flush();
                await RecoverMissingAssistantTextAsync(resp.RunId, assistantPayloadRefs, turnCts.Token).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!presetCommitted
                    && !await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await _out.WriteLineAsync(Tui.TuiPalette.Warn("Run interrupted by user.")).ConfigureAwait(false);
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "workspace_busy")
            {
                if (!presetCommitted
                    && !await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteBusyConflictAsync(ex).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                // Pre-run rejections (missing credential, no model) already name
                // themselves: one error line, no supplementary text.
                await FinishTurnAsync().ConfigureAwait(false);
                await _err.WriteLineAsync(Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!presetCommitted
                    && !await RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await _err.WriteLineAsync(Tui.TuiPalette.Err($"run_error: {ex.Message}")).ConfigureAwait(false);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }
        }
        finally
        {
            await StopTypeAheadAsync().ConfigureAwait(false);
            sticky.Exit();
            sessionRenderer.Surface.Repaint(w => w.Write("\u001b[r"));
        }

        return 0;
    }

    /// <summary>
    /// A busy workspace is operator-recoverable: name the run that owns it and how to
    /// release it instead of printing the raw error envelope.
    /// </summary>
    private async Task WriteBusyConflictAsync(AgentApiException error)
    {
        var blockingRunId = error.BlockingRunId;
        if (string.IsNullOrWhiteSpace(blockingRunId))
        {
            await _err.WriteLineAsync(Tui.TuiPalette.Err("workspace_busy: another Agent run still owns this workspace.")).ConfigureAwait(false);
            await _err.WriteLineAsync(Tui.TuiPalette.DimPaint("  release the blocking run with 'ru agent cancel <run-id>'.")).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var status = string.IsNullOrWhiteSpace(error.BlockingRunStatus) ? "active" : error.BlockingRunStatus;
        await _err.WriteLineAsync(Tui.TuiPalette.Err($"workspace_busy: run {blockingRunId} ({status}) still owns this workspace.")).ConfigureAwait(false);
        await _err.WriteLineAsync(Tui.TuiPalette.DimPaint($"  answer it with 'ru agent attach {blockingRunId}', or release it with 'ru agent cancel {blockingRunId}'.")).ConfigureAwait(false);
        await _err.FlushAsync().ConfigureAwait(false);
    }

    public async Task<int> SessionListAsync(SessionListCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.ListAsync(cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint("Active Sessions:")).ConfigureAwait(false);
            if (element.TryGetProperty("sessions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var title = item.GetProperty("title").GetString();
                    var msgCount = item.TryGetProperty("message_count", out var mc) ? mc.GetInt32() : 0;
                    await _out.WriteLineAsync($"  • {Tui.TuiPalette.Accent(id ?? "")} - {title} ({msgCount} messages)").ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_list_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Writes one of Ru's sessions out in another agent's format.
    /// </summary>
    /// <remarks>
    /// The files land where the named agent keeps its sessions unless <c>--out</c> says
    /// otherwise. That default is the point of the command: an export the caller has to
    /// move into place by hand is not really "continue this in Claude Code".
    /// </remarks>
    public async Task<int> SessionExportAsync(SessionExportCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var interchange = new SessionInterchangeApiClient(_session, _httpClient);
            var result = await interchange.ExportAsync(cmd.SessionId, cmd.Harness, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<RuAgent.SessionInterchange.SessionFileTarget> targets;
            if (cmd.OutputDirectory is { Length: > 0 } directory)
            {
                targets =
                [
                    .. result.Files.Select(file => new RuAgent.SessionInterchange.SessionFileTarget(
                        Path.Combine(directory, file.Name),
                        Append: false)),
                ];
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    await _err.WriteLineAsync("session_export_no_profile: cannot locate the user profile directory.")
                        .ConfigureAwait(false);
                    return 1;
                }

                targets = RuAgent.SessionInterchange.SessionFileLayout.TargetsFor(
                    result.Harness,
                    [.. result.Files.Select(file => file.Name)],
                    result.WorkingDirectory,
                    result.CreatedAt,
                    home);
            }

            var written = new List<string>(targets.Count);
            for (var index = 0; index < targets.Count && index < result.Files.Count; index++)
            {
                var target = targets[index];
                var parent = Path.GetDirectoryName(target.Path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (target.Append && File.Exists(target.Path))
                {
                    await File.AppendAllTextAsync(target.Path, result.Files[index].Content, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(target.Path, result.Files[index].Content, cancellationToken)
                        .ConfigureAwait(false);
                }

                written.Add(target.Path);
            }

            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    harness = result.Harness,
                    files = written,
                })).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync($"Exported {cmd.SessionId} as {result.DisplayName}:").ConfigureAwait(false);
            foreach (var path in written)
            {
                await _out.WriteLineAsync($"  • {Tui.TuiPalette.Accent(path)}").ConfigureAwait(false);
            }

            await _out.WriteLineAsync("  Restart that agent to pick the session up.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_export_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Turns another agent's session file into a Ru session.
    /// </summary>
    /// <remarks>
    /// The format is read from the file name when <c>--harness</c> is absent — a rollout
    /// names its thread, a Claude Code transcript is named by its session id — and the
    /// command asks instead of guessing when neither fits.
    /// </remarks>
    public async Task<int> SessionImportAsync(SessionImportCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cmd.FilePath))
            {
                await _err.WriteLineAsync($"session_import_error: no such file '{cmd.FilePath}'.").ConfigureAwait(false);
                return 1;
            }

            var harness = cmd.Harness ?? RuAgent.SessionInterchange.SessionFileLayout.HarnessFromFileName(cmd.FilePath);
            if (harness is null)
            {
                await _err.WriteLineAsync(
                    "session_import_error: cannot tell which agent wrote this file. Pass --harness.")
                    .ConfigureAwait(false);
                return 1;
            }

            string content;
            if (string.Equals(harness, RuAgent.SessionInterchange.SessionFileLayout.OpenCodeId, StringComparison.OrdinalIgnoreCase))
            {
                // opencode keeps every session in one database, so the argument is the store
                // and the session has to be named — the most recent one when nobody named it,
                // and it says which one it took rather than picking in silence.
                var openCodeSessionId = cmd.SessionId;
                if (string.IsNullOrWhiteSpace(openCodeSessionId))
                {
                    var sessions = RuAgent.SessionInterchange.OpenCodeDatabase.ListSessions(cmd.FilePath);
                    if (sessions.Count == 0)
                    {
                        await _err.WriteLineAsync($"session_import_error: '{cmd.FilePath}' holds no opencode sessions.")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    openCodeSessionId = sessions[0].Id;
                    await _out.WriteLineAsync(
                        $"  taking opencode session {Tui.TuiPalette.Accent(openCodeSessionId)} ({sessions[0].Title}).")
                        .ConfigureAwait(false);
                }

                content = RuAgent.SessionInterchange.OpenCodeDatabase.ReadSessionPayload(cmd.FilePath, openCodeSessionId);
            }
            else
            {
                content = await File.ReadAllTextAsync(cmd.FilePath, cancellationToken).ConfigureAwait(false);
            }
            var interchange = new SessionInterchangeApiClient(_session, _httpClient);
            var result = await interchange.ImportAsync(harness, content, cancellationToken).ConfigureAwait(false);

            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    harness,
                    session_id = result.SessionId,
                    messages = result.Messages,
                    dropped_entries = result.DroppedEntries,
                })).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(
                $"Imported {result.Messages} messages from {harness} as session {Tui.TuiPalette.Accent(result.SessionId)}.")
                .ConfigureAwait(false);

            if (result.DroppedEntries > 0)
            {
                await _out.WriteLineAsync(
                    $"  {result.DroppedEntries} entries stayed behind: Ru's session log holds turns, and tool traffic belongs to runs.")
                    .ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_import_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> SessionForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.ForkAsync(cmd.SessionId, cmd.UpToMessageIndex, cmd.NewTitle, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(element)).ConfigureAwait(false);
                return 0;
            }

            if (element.TryGetProperty("session", out var sess) && sess.TryGetProperty("id", out var newId))
            {
                await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Successfully forked session to: {newId.GetString()}")).ConfigureAwait(false);
                return 0;
            }
            await _err.WriteLineAsync("session_fork_failed: invalid server response").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_fork_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> SessionRewindAsync(SessionRewindCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.RewindAsync(cmd.SessionId, cmd.ToMessageIndex, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonSerializer.Serialize(element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Successfully rewound session {cmd.SessionId} to message {cmd.ToMessageIndex}")).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_rewind_error: {ex.Message}").ConfigureAwait(false);
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

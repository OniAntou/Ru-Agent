using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>
/// The run-start machinery shared by the CLI's run path, the interactive chat
/// path and the RPC bridge: preset snapshot/update/restore, run creation
/// validation helpers and workspace-trust handling. The writers are injected
/// so every message stays on the exact stream the caller used before.
/// </summary>
internal sealed class RunStartCore
{

    private readonly SessionApiClient _sessionClient;
    private readonly AgentApiClient _agentClient;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public RunStartCore(SessionApiClient sessionClient, AgentApiClient agentClient, TextWriter outWriter, TextWriter errWriter)
    {
        _sessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _out = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
        _err = errWriter ?? throw new ArgumentNullException(nameof(errWriter));
    }

    private static string CreateSnapshotToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"sps_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    internal sealed record SessionPresetUpdateResult(
        string SessionId,
        long Version,
        string SnapshotToken);

    internal async Task<SessionPresetUpdateResult?> UpdateSessionPresetAsync(
        string sessionId,
        string preset,
        bool interactive,
        CancellationToken cancellationToken)
    {
        var snapshotToken = CreateSnapshotToken();
        try
        {
            var response = await SessionPresetVersionNegotiator.ExecuteAsync(
                    sessionId,
                    expectedVersion => _agentClient.SetSessionPresetAsync(
                        sessionId,
                        preset,
                        expectedVersion,
                        interactive,
                        cancellationToken,
                        snapshotToken),
                    cancellationToken)
                .ConfigureAwait(false);
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

    internal async Task<bool> RestorePresetOrReportAsync(
        SessionPresetUpdateResult update)
    {
        if (await RestoreSessionPresetAsync(update, CancellationToken.None).ConfigureAwait(false))
        {
            return true;
        }

        await _err.WriteLineAsync("preset_restore_failed: refusing to continue.").ConfigureAwait(false);
        return false;
    }

    internal static bool IsAcceptedTrustStatus(string? status)
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
            "waiting_subagents" or
            "reconciliation_required" or
            "completed";
    }

    internal static bool IsKnownCreateStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() is
            "waiting_workspace_trust" or
            "waiting_history" or
            "waiting_history_user" or
            "waiting_history_final" or
            "running" or
            "waiting_approval" or
            "waiting_user" or
            "waiting_subagents" or
            "reconciliation_required" or
            "completed";
    }

    /// <summary>
    /// Checks that a create response is really bound to this session and preset before
    /// anything acts on it. Shared by the non-interactive trust path and the interactive
    /// prompt, so the interactive path cannot skip the binding check to reach the prompt.
    /// </summary>
    internal static bool IsCreateResponseBound(
        CreateAgentRunResponse response,
        string expectedSessionId,
        string expectedPreset) =>
        !string.IsNullOrWhiteSpace(response.RunId)
        && !string.IsNullOrWhiteSpace(response.SessionId)
        && !string.IsNullOrWhiteSpace(response.WorkspaceId)
        && string.Equals(response.SessionId, expectedSessionId, StringComparison.Ordinal)
        && string.Equals(response.Preset, expectedPreset, StringComparison.Ordinal)
        && IsKnownCreateStatus(response.Status);

    /// <summary>
    /// The non-interactive trust-required notice, kept in one place so the run
    /// and chat paths cannot drift. The bounded code stays first; the recovery
    /// hints name the automation flag before the manual approve command, and the
    /// parenthetical covers the moved/recreated case that reads a known folder
    /// as a brand-new identity.
    /// </summary>
    internal static string WorkspaceTrustRequiredMessage(string runId) =>
        "workspace_trust_required: this workspace identity is not trusted yet (a first-seen, moved, or recreated root needs one explicit approval). "
        + $"Rerun with --trust-workspace for automation, or use 'ru agent trust {runId} --approve'.";

    internal async Task<bool> HandleWorkspaceTrustAsync(
        CreateAgentRunResponse response,
        string expectedSessionId,
        string expectedPreset,
        bool trustWorkspace,
        bool json,
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
                await (json ? _err : _out).WriteLineAsync(
                    WorkspaceTrustRequiredMessage(response.RunId)).ConfigureAwait(false);
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
}

public sealed record RunStartRequest(
    string WorkspacePath,
    string Prompt,
    string Preset,
    string? SessionId,
    bool TrustWorkspace,
    bool ConfirmFullAccess,
    bool Json,
    IReadOnlyList<string>? ImageAttachments = null);

public sealed record RunStartOutcome(
    string RunId,
    string SessionId,
    string Status,
    string WorkspaceId);

/// <summary>A start-run failure whose exact message was already written by the core.</summary>
public sealed class RunStartException : Exception
{
    public RunStartException(string code, string line)
        : base(line)
    {
        Code = code;
        Line = line;
    }

    public string Code { get; }

    /// <summary>The exact single-line message the core wrote to the operator stream.</summary>
    public string Line { get; }
}

/// <summary>
/// Runs the shared start-a-run sequence: preset validation and CAS update,
/// session creation, run creation and workspace-trust handling.
/// </summary>
public sealed class RunStarter
{
    private readonly SessionApiClient _sessionClient;
    private readonly AgentApiClient _agentClient;
    private readonly RunStartCore _core;
    private readonly RecordingWriter _out;
    private readonly RecordingWriter _err;
    private readonly SettingsApiClient? _settingsClient;

    public RunStarter(
        SessionApiClient sessionClient,
        AgentApiClient agentClient,
        TextWriter outWriter,
        TextWriter errWriter,
        SettingsApiClient? settingsClient = null)
    {
        _sessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _out = new RecordingWriter(outWriter ?? throw new ArgumentNullException(nameof(outWriter)));
        _err = new RecordingWriter(errWriter ?? throw new ArgumentNullException(nameof(errWriter)));
        _settingsClient = settingsClient;
        _core = new RunStartCore(sessionClient, agentClient, _out, _err);
    }

    internal RunStartCore Core => _core;

    public async Task<RunStartOutcome> StartAsync(
        RunStartRequest request,
        Func<string, Task>? onSessionCreated,
        CancellationToken cancellationToken,
        Func<RunStartOutcome, Task>? onRunCreated = null)
    {
        _out.Reset();
        _err.Reset();

        if (!RuAgent.Protocol.Harness.AgentPolicyProfileCatalog.TryNormalizePresetInput(request.Preset, out var normalizedPreset))
        {
            await _err.WriteLineAsync("invalid_preset: preset must be plan, standard, or full_access").ConfigureAwait(false);
            throw new RunStartException("invalid_preset", "invalid_preset: preset must be plan, standard, or full_access");
        }

        // Every payload below binds the canonical wire value. Validating loosely
        // while still sending the raw string ("Standard", " standard") would only
        // move the failure from here to the server's response binding.
        request = request with { Preset = normalizedPreset };

        if (request.Preset == "full_access" && !request.ConfirmFullAccess)
        {
            await _err.WriteLineAsync("full_access_confirmation_required: --confirm-full-access is required.").ConfigureAwait(false);
            throw new RunStartException("full_access_confirmation_required", "full_access_confirmation_required: --confirm-full-access is required.");
        }

        string canonicalWorkspace;
        try
        {
            canonicalWorkspace = Path.GetFullPath(request.WorkspacePath);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"invalid_workspace: {ex.Message}").ConfigureAwait(false);
            throw new RunStartException("invalid_workspace", $"invalid_workspace: {ex.Message}");
        }
        if (canonicalWorkspace.Length > AgentCliParser.MaxWorkspacePathLength)
        {
            await _err.WriteLineAsync("invalid_workspace: path too long").ConfigureAwait(false);
            throw new RunStartException("invalid_workspace", "invalid_workspace: path too long");
        }

        if (_settingsClient is not null
            && await ModelPrecheck.CheckCurrentAsync(_settingsClient, cancellationToken).ConfigureAwait(false) is { } modelWarning)
        {
            // One warning, once per run start: a models.dev id missing from the catalog
            // would otherwise only surface mid-run as model_unavailable. The check is
            // awaited before the session is created — one settings read and, for
            // models.dev, one catalog read — not a per-model-step cost.
            await (request.Json ? _err : _out).WriteLineAsync(modelWarning).ConfigureAwait(false);
        }

        string sessionId;
        if (request.SessionId is not null)
        {
            sessionId = request.SessionId;
        }
        else
        {
            try
            {
                sessionId = await _sessionClient.CreateAsync(cancellationToken).ConfigureAwait(false);
                // --json owns stdout: human-facing ids go to stderr so the NDJSON
                // trace stays machine-parseable line by line.
                if (onSessionCreated is not null)
                {
                    await onSessionCreated(sessionId).ConfigureAwait(false);
                }
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                throw new RunStartException(ex.ErrorCode, CliErrorText.Format(ex));
            }
        }

        var presetUpdate = await _core.UpdateSessionPresetAsync(
            sessionId,
            request.Preset,
            request.ConfirmFullAccess,
            cancellationToken).ConfigureAwait(false);
        if (presetUpdate is null)
        {
            var line = _err.LastLine ?? "preset_update_failed";
            throw new RunStartException(PresetUpdateFailureCode(line), line);
        }

        var createRequest = new CreateAgentRunRequest(
            sessionId,
            canonicalWorkspace,
            request.Prompt,
            request.Preset,
            presetUpdate.Version,
            Guid.NewGuid().ToString("N"),
            request.ImageAttachments);

        CreateAgentRunResponse response;
        try
        {
            response = await _agentClient.CreateRunAsync(createRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException ex)
        {
            // Both sides matter here: dev restores the preset a failed create left
            // behind, and CliErrorText renders the server body as one bounded line
            // instead of raw JSON.
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                throw new RunStartException(ex.ErrorCode, CliErrorText.Format(ex));
            }

            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            throw new RunStartException(ex.ErrorCode, CliErrorText.Format(ex));
        }
        catch (OperationCanceledException)
        {
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                throw;
            }

            throw;
        }
        catch (Exception ex)
        {
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"agent_create_failed: {ex.Message}").ConfigureAwait(false);
                throw new RunStartException("agent_create_failed", $"agent_create_failed: {ex.Message}");
            }

            await _err.WriteLineAsync($"agent_create_failed: {ex.Message}").ConfigureAwait(false);
            throw new RunStartException("agent_create_failed", $"agent_create_failed: {ex.Message}");
        }

        bool trustAccepted;
        try
        {
            if (onRunCreated is not null)
            {
                await onRunCreated(new RunStartOutcome(response.RunId, response.SessionId, response.Status, response.WorkspaceId)).ConfigureAwait(false);
            }

            trustAccepted = await _core.HandleWorkspaceTrustAsync(
                response,
                sessionId,
                request.Preset,
                request.TrustWorkspace,
                request.Json,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                throw;
            }

            throw;
        }
        catch (Exception ex)
        {
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                await _err.WriteLineAsync($"agent_output_failed: {ex.Message}").ConfigureAwait(false);
                throw new RunStartException("agent_output_failed", $"agent_output_failed: {ex.Message}");
            }

            await _err.WriteLineAsync($"agent_output_failed: {ex.Message}").ConfigureAwait(false);
            throw new RunStartException("agent_output_failed", $"agent_output_failed: {ex.Message}");
        }
        if (!trustAccepted)
        {
            var code = string.Equals(response.Status, "waiting_workspace_trust", StringComparison.OrdinalIgnoreCase)
                && !request.TrustWorkspace
                ? "workspace_trust_required"
                : "workspace_trust_failed";
            var line = code == "workspace_trust_required"
                ? (request.Json ? _err.LastLine : _out.LastLine) ?? "workspace_trust_required"
                : _err.LastLine ?? "workspace_trust_failed";
            if (!await _core.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
            {
                throw new RunStartException(code, line);
            }

            throw new RunStartException(code, line);
        }

        return new RunStartOutcome(response.RunId, response.SessionId, response.Status, response.WorkspaceId);
    }

    private static string PresetUpdateFailureCode(string line)
    {
        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        return separator > 0 ? line[..separator] : "preset_update_failed";
    }

    /// <summary>
    /// Forwards to the caller's writer and remembers the last line, so a failure
    /// that was reported deep inside the core can still be raised as a
    /// <see cref="RunStartException"/> carrying the exact operator-facing text.
    /// </summary>
    private sealed class RecordingWriter : TextWriter
    {
        private readonly TextWriter _inner;

        public RecordingWriter(TextWriter inner)
        {
            _inner = inner;
        }

        public string? LastLine { get; private set; }

        public override Encoding Encoding => _inner.Encoding;

        public void Reset() => LastLine = null;

        public override void Write(char value) => _inner.Write(value);

        public override void Write(string? value) => _inner.Write(value);

        public override void WriteLine(string? value)
        {
            LastLine = value;
            _inner.WriteLine(value);
        }

        public override Task WriteLineAsync(string? value)
        {
            LastLine = value;
            return _inner.WriteLineAsync(value);
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync() => _inner.FlushAsync();
    }
}

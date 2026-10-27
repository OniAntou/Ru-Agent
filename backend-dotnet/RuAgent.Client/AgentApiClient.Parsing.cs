using System.Text;
using System.Text.Json;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed partial class AgentApiClient
{
    private static JsonElement ParseJsonClone(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            // Callers catch AgentApiException; a raw JsonException from a malformed
            // body would otherwise escape every command's error handling.
            throw new AgentApiException("invalid_response", exception.Message);
        }
    }

    private static JsonElement ParseRestoreResponseStrict(string json, string expectedSessionId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgentApiException("invalid_response", "Restore response must be an object.");
            }

            var allowed = new HashSet<string>(
                ["status", "session_id", "preset", "version", "restored", "replayed"],
                StringComparer.Ordinal);
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
                {
                    throw new AgentApiException("invalid_response", "Restore response contains an unknown or duplicate field.");
                }
            }

            if (fields.Count != allowed.Count
                || !fields.TryGetValue("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !string.Equals(status.GetString(), "ok", StringComparison.Ordinal)
                || !fields.TryGetValue("session_id", out var session)
                || session.ValueKind != JsonValueKind.String
                || !string.Equals(session.GetString(), expectedSessionId, StringComparison.Ordinal)
                || !fields.TryGetValue("restored", out var restored)
                || restored.ValueKind != JsonValueKind.True
                || !fields.TryGetValue("replayed", out var replayed)
                || replayed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !fields.TryGetValue("preset", out var preset)
                || preset.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !fields.TryGetValue("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt64(out var versionValue)
                || versionValue < 0)
            {
                throw new AgentApiException("invalid_response", "Invalid restore response.");
            }

            if (preset.ValueKind == JsonValueKind.Null)
            {
                if (versionValue != 0)
                {
                    throw new AgentApiException("invalid_response", "Invalid restore version.");
                }
            }
            else if (versionValue <= 0
                || !RuAgent.Protocol.Harness.AgentPolicyProfileCatalog.IsKnownPreset(preset.GetString()))
            {
                throw new AgentApiException("invalid_response", "Invalid restore selection.");
            }

            return root.Clone();
        }
    }

    private static CreateAgentRunResponse ParseCreateResponseStrict(
        string json,
        string expectedSessionId,
        string expectedPreset)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            var allowed = new HashSet<string>(["run_id", "session_id", "workspace_id", "status", "preset", "created_at", "updated_at", "policy_snapshot_version", "policy_snapshot_sha256", "version", "last_sequence", "history_wait_kind", "terminal_at", "terminal_error_code", "trust_request_id", "display_identity_hash", "provider_backend", "provider_model"], StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                if (!allowed.Contains(prop.Name))
                    throw new AgentApiException("unknown_field", $"Unexpected field {prop.Name}");
            }
            ValidateProviderPins(root);
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<CreateAgentRunResponse>(json, options);
                if (result is null
                    || string.IsNullOrWhiteSpace(result.RunId)
                    || string.IsNullOrWhiteSpace(result.SessionId)
                    || string.IsNullOrWhiteSpace(result.WorkspaceId)
                    || !string.Equals(result.SessionId, expectedSessionId, StringComparison.Ordinal)
                    || !string.Equals(result.Preset, expectedPreset, StringComparison.Ordinal)
                    || !IsKnownCreateStatus(result.Status)
                    || result.Status.Equals("waiting_workspace_trust", StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrWhiteSpace(result.TrustRequestId)
                            || string.IsNullOrWhiteSpace(result.DisplayIdentityHash)))
                {
                    throw new AgentApiException("invalid_response", "Invalid create response binding.");
                }

                ValidateRunId(result.RunId);
                if (result.TrustRequestId is not null && string.IsNullOrWhiteSpace(result.TrustRequestId)
                    || result.DisplayIdentityHash is not null && string.IsNullOrWhiteSpace(result.DisplayIdentityHash))
                {
                    throw new AgentApiException("invalid_response", "Invalid challenge identifiers.");
                }

                return result;
            }
            catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        }
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
            "waiting_subagents" or
            "reconciliation_required" or
            "completed";
    }

    private static GetAgentRunResponse ParseGetResponseStrict(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
        var root = doc.RootElement;
        var allowed = new HashSet<string>(["run_id", "session_id", "workspace_id", "status", "history_wait_kind", "preset", "policy_snapshot_version", "policy_snapshot_sha256", "version", "last_sequence", "created_at", "updated_at", "terminal_at", "terminal_error_code", "trust_request_id", "display_identity_hash", "provider_backend", "provider_model", "provider_protocol", "provider_model_label", "provider_calling_mode", "context_window_tokens", "context_window_source", "context_window_version", "output_token_reserve", "tool_profile"], StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                throw new AgentApiException("unknown_field", $"Unexpected field {prop.Name}");
        }
        ValidateProviderPins(root);
        ValidateRunMetadata(root);
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<GetAgentRunResponse>(json, options);
            if (result is null || string.IsNullOrWhiteSpace(result.RunId))
                throw new AgentApiException("invalid_response", "Missing run_id");
            ValidateRunId(result.RunId);
            if (result.PolicySnapshotVersion <= 0) throw new AgentApiException("invalid_response", "invalid snapshot version");
            return result;
        }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        }
    }

    private static void ValidateProviderPins(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("provider_backend" or "provider_model"))
            {
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                throw new AgentApiException("invalid_response", $"Invalid {property.Name}");
            }

            var pin = property.Value.GetString();
            var isModelsDevBackend = property.Name == "provider_backend"
                && pin?.StartsWith(ProviderCredential.ModelsDevPrefix, StringComparison.Ordinal) is true;
            var maxChars = property.Name == "provider_model"
                ? MaxProviderModelChars
                : isModelsDevBackend ? MaxModelsDevBackendChars : MaxProviderBackendChars;
            if (pin is not null && pin.Length > maxChars)
            {
                throw new AgentApiException("invalid_response", $"Invalid {property.Name}");
            }
        }
    }

    private static void ValidateRunMetadata(JsonElement root)
    {
        ValidateOptionalString(root, "provider_protocol", 64);
        ValidateOptionalString(root, "tool_profile", 32);
        ValidateOptionalString(root, "provider_model_label", 256);
        ValidateOptionalString(root, "context_window_source", 64);
        ValidateOptionalString(root, "context_window_version", 128);
        ValidateOptionalInteger(root, "context_window_tokens", 1, 10_000_000);
        ValidateOptionalInteger(root, "output_token_reserve", 1, 128_000);
        if (root.TryGetProperty("provider_calling_mode", out var mode)
            && mode.ValueKind is not JsonValueKind.Null
            && (mode.ValueKind != JsonValueKind.String
                || mode.GetString() is not ("native" or "text_selection")))
            throw new AgentApiException("invalid_response", "Invalid provider_calling_mode");
    }

    private static bool HasString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool HasInteger(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out _);

    private static void ValidateOptionalString(JsonElement root, string name, int maximum, bool utf8Bytes = false)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_response", $"Invalid {name}");
        var text = value.GetString()!;
        if ((utf8Bytes ? Encoding.UTF8.GetByteCount(text) : text.Length) > maximum)
            throw new AgentApiException("invalid_response", $"Invalid {name}");
    }

    private static void ValidateOptionalInteger(JsonElement root, string name, long minimum, long maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number < minimum || number > maximum)
            throw new AgentApiException("invalid_response", $"Invalid {name}");
    }

    private static void ValidateOptionalBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AgentApiException("invalid_response", $"Invalid {name}");
    }

    private static CompactAgentRunResponse ParseCompactResponseStrict(
        string json,
        string expectedRunId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }

        using (document)
        {
            var allowed = new HashSet<string>(
                ["run_id", "status", "strategy", "compaction_id", "before_tokens", "after_tokens", "run_version"],
                StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new AgentApiException(
                        "unknown_field",
                        $"Unexpected field {property.Name}");
                }
            }
        }

        try
        {
            var result = JsonSerializer.Deserialize<CompactAgentRunResponse>(json, JsonOptions);
            if (result is null
                || !string.Equals(result.RunId, expectedRunId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.Status)
                || result.Strategy is not ("normal" or "aggressive")
                || result.RunVersion < 0
                || (result.CompactionId is not null && !IsBoundedToken(result.CompactionId))
                || (result.BeforeTokens is null) != (result.AfterTokens is null)
                || (result.BeforeTokens is not null
                    && result.AfterTokens is not null
                    && (result.BeforeTokens < 0 || result.AfterTokens < 0
                        || result.AfterTokens > result.BeforeTokens)))
            {
                throw new AgentApiException("invalid_response", "Invalid compaction response.");
            }

            ValidateRunId(result.RunId);
            return result;
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }
    }

    private static DispatchAgentCommandResponse ParseDispatchCommandResponseStrict(
        string json,
        string expectedRunId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AgentApiException("invalid_response", "Command response must be an object.");
            }

            var allowed = new HashSet<string>(
                ["run_id", "status", "code", "data", "run_version"],
                StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new AgentApiException("unknown_field", $"Unexpected field {property.Name}");
                }
            }
        }

        try
        {
            var result = JsonSerializer.Deserialize<DispatchAgentCommandResponse>(json, JsonOptions);
            if (result is null
                || !string.Equals(result.RunId, expectedRunId, StringComparison.Ordinal)
                || !ToolResultStatuses.Known.Contains(result.Status)
                || !IsBoundedToken(result.Code)
                || result.RunVersion < 0)
            {
                throw new AgentApiException("invalid_response", "Invalid command response.");
            }

            ValidateRunId(result.RunId);
            return result;
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }
    }
}

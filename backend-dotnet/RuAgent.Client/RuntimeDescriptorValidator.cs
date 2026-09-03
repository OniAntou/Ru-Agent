using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public static class RuntimeDescriptorValidator
{
    public static RuntimeDescriptor ParseStrict(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AgentApiException("invalid_descriptor", "Descriptor empty.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new AgentApiException("invalid_descriptor", ex.Message); }
        using (doc)
        {

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AgentApiException("invalid_descriptor", "Root must be object.");

        var fields = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!fields.SetEquals(RuntimeDescriptor.AllowedFields))
        {
            var extra = fields.Except(RuntimeDescriptor.AllowedFields).FirstOrDefault();
            if (extra is not null) throw new AgentApiException("unknown_field", $"Unknown field: {extra}");
            var missing = RuntimeDescriptor.AllowedFields.Except(fields).FirstOrDefault();
            if (missing is not null) throw new AgentApiException("missing_field", $"Missing field: {missing}");
        }

        if (!root.TryGetProperty("protocol_version", out var vProp) || vProp.ValueKind != JsonValueKind.Number || !vProp.TryGetInt32(out var version))
            throw new AgentApiException("invalid_protocol_version", "protocol_version invalid");
        if (version != RuntimeDescriptor.CurrentProtocolVersion)
            throw new AgentApiException("unsupported_protocol_version", $"Unsupported version {version}");

        if (!root.TryGetProperty("base_url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_base_url", "base_url invalid");
        var baseUrl = urlProp.GetString()!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new AgentApiException("invalid_base_url", "base_url must be loopback http");
        if (uri.Port is < 1024 or > 65535)
            throw new AgentApiException("invalid_base_url", "port invalid");

        if (!root.TryGetProperty("bearer_token", out var bearerProp) || bearerProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_bearer_token", "bearer_token invalid");
        var bearer = bearerProp.GetString()!;
        if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 4096)
            throw new AgentApiException("invalid_bearer_token", "bearer_token invalid");

        if (!root.TryGetProperty("pid", out var pidProp) || pidProp.ValueKind != JsonValueKind.Number || !pidProp.TryGetInt32(out var pid) || pid <= 0)
            throw new AgentApiException("invalid_pid", "pid invalid");

        if (!root.TryGetProperty("process_start_time", out var startProp) || startProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_process_start_time", "process_start_time invalid");
        var startTime = startProp.GetString()!;
        if (!DateTimeOffset.TryParse(startTime, out _))
            throw new AgentApiException("invalid_process_start_time", "process_start_time parse failed");

        if (!root.TryGetProperty("instance_nonce", out var nonceProp) || nonceProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_instance_nonce", "instance_nonce invalid");
        var nonce = nonceProp.GetString()!;
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length != 32)
            throw new AgentApiException("invalid_instance_nonce", "instance_nonce length must be 32");

        if (!root.TryGetProperty("created_at", out var createdProp) || createdProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_created_at", "created_at invalid");
        var created = createdProp.GetString()!;
        if (!DateTimeOffset.TryParse(created, out _))
            throw new AgentApiException("invalid_created_at", "created_at parse failed");

        // Ensure no forbidden leakage (worker/broker tokens etc) – but we already checked allowed fields, so any extra would have failed.
        // Additionally ensure bearer does not contain provider secrets? Not needed.

        return new RuntimeDescriptor(version, baseUrl, bearer, pid, startTime, nonce, created);
        }
    }

    public static bool TryParseStrict(string json, out RuntimeDescriptor? descriptor, out string? error)
    {
        try
        {
            descriptor = ParseStrict(json);
            error = null;
            return true;
        }
        catch (AgentApiException ex)
        {
            descriptor = null;
            error = ex.ErrorCode;
            return false;
        }
        catch (Exception ex)
        {
            descriptor = null;
            error = ex.Message;
            return false;
        }
    }
}

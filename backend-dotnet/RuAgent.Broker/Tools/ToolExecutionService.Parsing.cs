using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private static string ReadCapabilityClass(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("capability_class", out var node) || node is null)
        {
            return "internal";
        }

        return node is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result)
            && result.Length <= 64
                ? result
                : throw new ToolExecutionException("invalid_capability_class");
    }

    private static string ReadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new ToolExecutionException("invalid_arguments");

    private static bool ReadBool(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<bool>(out var result)
            ? result
            : throw new ToolExecutionException("invalid_arguments");

    private static int? ReadOptionalInt(JsonObject payload, string key)
    {
        if (payload[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var result))
        {
            return result;
        }

        if (value.TryGetValue<long>(out var longResult) && longResult is >= int.MinValue and <= int.MaxValue)
        {
            return checked((int)longResult);
        }

        return null;
    }

    private static bool IsTrue(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<bool>(out var flag)
        && flag;

    private static string? ReadOptionalString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    private static string? ReadAuthorizationToken(JsonObject payload) =>
        ReadOptionalString(payload, "authorization_token")
        ?? ReadOptionalString(payload, "authorizationToken")
        ?? ReadOptionalString(payload, "approval_token");

    private static IReadOnlyList<string> ReadArguments(JsonNode? node)
    {
        // The run-mode schema makes the argument vector optional.
        if (node is null) return Array.Empty<string>();

        if (node is not JsonArray values || values.Count > 64)
        {
            throw new ToolExecutionException("invalid_arguments");
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Length > 1024)
            {
                throw new ToolExecutionException("invalid_arguments");
            }

            result.Add(text);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadWriteSet(JsonObject payload)
    {
        if (payload["write_set"] is null)
        {
            return Array.Empty<string>();
        }

        if (payload["write_set"] is not JsonArray values)
        {
            throw new ToolExecutionException("invalid_terminal_write_set");
        }

        if (values.Count > 256)
        {
            throw new ToolExecutionException("invalid_terminal_write_set");
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonValue item
                || !item.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path)
                || path.Length > 4096)
            {
                throw new ToolExecutionException("invalid_terminal_write_set");
            }

            result.Add(path);
        }

        try
        {
            WorkspaceWriteSet.Validate(IsTrue(payload, "read_only"), result);
        }
        catch (AgentProtocolException exception)
        {
            throw new ToolExecutionException(exception.Code);
        }

        return result;
    }
}

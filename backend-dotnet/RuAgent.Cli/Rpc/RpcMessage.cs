using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Cli.Rpc;

public sealed record RpcRequest(JsonNode? Id, string Method, JsonElement Params)
{
    public bool IsNotification => Id is null;
}

/// <summary>Builds and parses the JSON-RPC 2.0 envelope. Pure: no I/O, no host state.</summary>
public static class RpcMessage
{
    private static readonly JsonElement EmptyParams = JsonDocument.Parse("{}").RootElement.Clone();

    public static RpcRequest? TryParseRequest(string line, out JsonObject? error)
    {
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = ErrorResponse(null, RpcProtocol.ParseError, $"parse_error: {ex.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = ErrorResponse(null, RpcProtocol.InvalidRequest, "invalid_request: root must be an object.");
                return null;
            }

            JsonNode? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                id = idElement.ValueKind switch
                {
                    JsonValueKind.String or JsonValueKind.Number => JsonNode.Parse(idElement.GetRawText()),
                    _ => null,
                };
                if (id is null)
                {
                    error = ErrorResponse(null, RpcProtocol.InvalidRequest, "invalid_request: id must be a string or number.");
                    return null;
                }
            }

            if (!root.TryGetProperty("jsonrpc", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || versionElement.GetString() != "2.0")
            {
                error = ErrorResponse(id, RpcProtocol.InvalidRequest, "invalid_request: jsonrpc must be \"2.0\".");
                return null;
            }

            if (!root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || methodElement.GetString() is not { Length: > 0 and <= RpcProtocol.MaxMethodLength } method)
            {
                error = ErrorResponse(id, RpcProtocol.InvalidRequest, "invalid_request: method must be a bounded string.");
                return null;
            }

            var parameters = EmptyParams;
            if (root.TryGetProperty("params", out var paramsElement))
            {
                if (paramsElement.ValueKind != JsonValueKind.Object)
                {
                    error = ErrorResponse(id, RpcProtocol.InvalidParams, "invalid_params: params must be an object.");
                    return null;
                }

                parameters = paramsElement.Clone();
            }

            return new RpcRequest(id, method, parameters);
        }
    }

    public static JsonObject SuccessResponse(JsonNode? id, JsonNode? result) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result ?? new JsonObject(),
        };

    public static JsonObject ErrorResponse(JsonNode? id, int code, string message, string? ruCode = null, int? httpStatus = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        var data = new JsonObject();
        if (ruCode is not null)
        {
            data["ruCode"] = ruCode;
        }
        if (httpStatus is not null)
        {
            data["httpStatus"] = httpStatus;
        }
        if (data.Count > 0)
        {
            error["data"] = data;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = error,
        };
    }

    public static JsonObject Notification(string method, JsonNode parameters) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };
}

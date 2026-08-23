using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Desktop;

public sealed record VolumeState(int Level, bool Muted);

public interface IVolumeController
{
    IVolumeEndpoint OpenDefaultRenderEndpoint();
}

public interface IVolumeEndpoint : IDisposable
{
    string EndpointRole { get; }
    string EndpointHash { get; }
    VolumeState GetState();
    void SetLevel(int level);
    void SetMuted(bool muted);
}

public sealed class VolumeControllerException : Exception
{
    public VolumeControllerException(string code) : base(code) => Code = code;

    public string Code { get; }
}

public sealed class VolumeToolException : Exception
{
    public VolumeToolException(string code, string safeSummary) : base(code)
    {
        Code = code;
        SafeSummary = safeSummary;
    }

    public string Code { get; }
    public string SafeSummary { get; }
}

public sealed class VolumeTools
{
    private const int LegacyRelativeAmount = 4;
    private static readonly IReadOnlySet<string> Operations = new HashSet<string>(
        ["get", "set", "increase", "decrease", "mute", "unmute"],
        StringComparer.Ordinal);

    private readonly IVolumeController _controller;

    public VolumeTools(IVolumeController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public static ToolManifest Manifest { get; } = new(
        "system.volume",
        "1.0.0",
        "desktop",
        RiskClass.Reversible,
        new HashSet<string>([ToolScope.SystemWrite], StringComparer.Ordinal),
        AgentModelToolContractCatalog.Get("system.volume", "1.0.0").InputSchema,
        new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("operation", "level", "muted"),
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject { ["type"] = "string" },
                ["level"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100 },
                ["muted"] = new JsonObject { ["type"] = "boolean" },
            },
        },
        10_000,
        16 * 1024,
        "builtin");

    public ToolExecutionOutcome Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        VolumeRequest request;
        try
        {
            request = Parse(arguments);
        }
        catch (VolumeToolException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.SafeSummary);
        }

        string? endpointRole = null;
        string? endpointHash = null;
        try
        {
            using var endpoint = _controller.OpenDefaultRenderEndpoint();
            endpointRole = endpoint.EndpointRole;
            endpointHash = endpoint.EndpointHash;
            var before = endpoint.GetState();
            if (request.Operation == "get")
            {
                return ToolExecutionOutcome.Succeeded(
                    Output(request.Operation, before),
                    ToolVerificationStates.Verified,
                    "Volume state verified.",
                    endpointRole,
                    endpointHash);
            }

            Apply(request, before, endpoint, out var expectedLevel, out var expectedMuted);
            var after = endpoint.GetState();
            if (after.Level != expectedLevel || after.Muted != expectedMuted)
            {
                return ToolExecutionOutcome.Failed(
                    "readback_mismatch",
                    "Volume change could not be verified.",
                    ToolVerificationStates.Failed,
                    endpointRole,
                    endpointHash);
            }

            return ToolExecutionOutcome.Succeeded(
                Output(request.Operation, after),
                ToolVerificationStates.Verified,
                "Volume change verified.",
                endpointRole,
                endpointHash);
        }
        catch (VolumeControllerException error)
        {
            return ToolExecutionOutcome.Failed(
                error.Code,
                SafeSummary(error.Code),
                ToolVerificationStates.Failed,
                endpointRole,
                endpointHash);
        }
        catch (Exception)
        {
            return ToolExecutionOutcome.Failed(
                "adapter_error",
                SafeSummary("adapter_error"),
                ToolVerificationStates.Failed,
                endpointRole,
                endpointHash);
        }
    }

    private static void Apply(
        VolumeRequest request,
        VolumeState before,
        IVolumeEndpoint endpoint,
        out int expectedLevel,
        out bool expectedMuted)
    {
        expectedLevel = before.Level;
        expectedMuted = before.Muted;
        switch (request.Operation)
        {
            case "set":
                expectedLevel = request.Level!.Value;
                endpoint.SetLevel(expectedLevel);
                break;
            case "increase":
                expectedLevel = Math.Min(100, before.Level + request.Amount!.Value);
                endpoint.SetLevel(expectedLevel);
                break;
            case "decrease":
                expectedLevel = Math.Max(0, before.Level - request.Amount!.Value);
                endpoint.SetLevel(expectedLevel);
                break;
            case "mute":
                expectedMuted = true;
                endpoint.SetMuted(true);
                break;
            case "unmute":
                expectedMuted = false;
                endpoint.SetMuted(false);
                break;
        }
    }

    private static VolumeRequest Parse(JsonObject arguments)
    {
        if (arguments.Any(item => item.Key is not ("operation" or "level" or "amount")))
        {
            throw InvalidArguments();
        }

        var operation = arguments["operation"] is JsonValue operationValue
            && operationValue.TryGetValue<string>(out var operationText)
            ? operationText.Trim().ToLowerInvariant()
            : throw InvalidArguments();
        if (!Operations.Contains(operation))
        {
            throw InvalidArguments();
        }

        if (operation == "set")
        {
            if (arguments.ContainsKey("amount") || !ReadInt(arguments, "level", 0, 100, out var level))
            {
                throw InvalidArguments();
            }

            return new VolumeRequest(operation, level, null);
        }

        if (operation is "increase" or "decrease")
        {
            if (arguments.ContainsKey("level"))
            {
                throw InvalidArguments();
            }

            var amount = arguments.ContainsKey("amount")
                ? ReadInt(arguments, "amount", 1, 50, out var parsedAmount)
                    ? parsedAmount
                    : throw InvalidArguments()
                : LegacyRelativeAmount;
            return new VolumeRequest(operation, null, amount);
        }

        if (arguments.ContainsKey("level") || arguments.ContainsKey("amount"))
        {
            throw InvalidArguments();
        }

        return new VolumeRequest(operation, null, null);
    }

    private static bool ReadInt(JsonObject arguments, string key, int minimum, int maximum, out int value)
    {
        value = 0;
        if (arguments[key] is not JsonValue node || !node.TryGetValue<int>(out value))
        {
            return false;
        }

        return value >= minimum && value <= maximum;
    }

    private static JsonObject Output(string operation, VolumeState state) => new()
    {
        ["operation"] = operation,
        ["level"] = state.Level,
        ["muted"] = state.Muted,
    };

    private static VolumeToolException InvalidArguments() =>
        new("invalid_arguments", "Invalid volume request.");

    private static string SafeSummary(string code) => code switch
    {
        "unsupported_platform" => "Volume control is only available on Windows.",
        "default_endpoint_unavailable" => "The default audio endpoint is unavailable.",
        "read_failed" => "The current volume could not be read.",
        "write_failed" => "The volume change could not be written.",
        _ => "The volume operation could not be completed.",
    };

    private sealed record VolumeRequest(string Operation, int? Level, int? Amount);
}

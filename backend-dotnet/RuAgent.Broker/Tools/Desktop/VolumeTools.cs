using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Tools.Desktop;

public sealed record VolumeState(int Level, bool Muted);

public interface IVolumeController
{
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
        new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("operation"),
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("get", "set", "increase", "decrease", "mute", "unmute"),
                },
                ["level"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100 },
                ["amount"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50 },
            },
        },
        new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("operation", "level", "muted", "verified"),
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject { ["type"] = "string" },
                ["level"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100 },
                ["muted"] = new JsonObject { ["type"] = "boolean" },
                ["verified"] = new JsonObject { ["const"] = true },
            },
        },
        10_000,
        16 * 1024,
        "builtin");

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var request = Parse(arguments);
        VolumeState? before = null;
        try
        {
            before = _controller.GetState();
            if (request.Operation == "get")
            {
                return Output(request.Operation, before);
            }

            var expectedLevel = before.Level;
            var expectedMuted = before.Muted;
            switch (request.Operation)
            {
                case "set":
                    expectedLevel = request.Level!.Value;
                    _controller.SetLevel(expectedLevel);
                    break;
                case "increase":
                    expectedLevel = Math.Min(100, before.Level + request.Amount!.Value);
                    _controller.SetLevel(expectedLevel);
                    break;
                case "decrease":
                    expectedLevel = Math.Max(0, before.Level - request.Amount!.Value);
                    _controller.SetLevel(expectedLevel);
                    break;
                case "mute":
                    expectedMuted = true;
                    _controller.SetMuted(true);
                    break;
                case "unmute":
                    expectedMuted = false;
                    _controller.SetMuted(false);
                    break;
            }

            var after = _controller.GetState();
            if (after.Level != expectedLevel || after.Muted != expectedMuted)
            {
                throw new VolumeToolException("readback_mismatch", "Volume change could not be verified.");
            }

            return Output(request.Operation, after);
        }
        catch (VolumeToolException)
        {
            throw;
        }
        catch (VolumeControllerException error)
        {
            throw new VolumeToolException(error.Code, SafeSummary(error.Code));
        }
        catch (Exception)
        {
            throw new VolumeToolException("adapter_error", SafeSummary("adapter_error"));
        }
    }

    private static VolumeRequest Parse(JsonObject arguments)
    {
        if (arguments.Any(item => item.Key is not ("operation" or "level" or "amount")))
        {
            throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
        }

        var operation = arguments["operation"] is JsonValue operationValue
            && operationValue.TryGetValue<string>(out var operationText)
            ? operationText.Trim().ToLowerInvariant()
            : throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
        if (!Operations.Contains(operation))
        {
            throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
        }

        if (operation == "set")
        {
            if (arguments.ContainsKey("amount") || !ReadInt(arguments, "level", 0, 100, out var level))
            {
                throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
            }

            return new VolumeRequest(operation, level, null);
        }

        if (operation is "increase" or "decrease")
        {
            if (arguments.ContainsKey("level"))
            {
                throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
            }

            var amount = arguments.ContainsKey("amount")
                ? ReadInt(arguments, "amount", 1, 50, out var parsedAmount) ? parsedAmount : throw new VolumeToolException("invalid_arguments", "Invalid volume request.")
                : LegacyRelativeAmount;
            return new VolumeRequest(operation, null, amount);
        }

        if (arguments.ContainsKey("level") || arguments.ContainsKey("amount"))
        {
            throw new VolumeToolException("invalid_arguments", "Invalid volume request.");
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
        ["verified"] = true,
    };

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

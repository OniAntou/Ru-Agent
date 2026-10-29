using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    private readonly record struct TerminalMetadata(string? CanonicalIdentity, int? CompletedReadExitCode,
        string? RepairIdentity, bool InvocationInvalid, string? WorkingDirectoryIdentity, string? WorkingDirectory);

    private static TerminalMetadata ReadTerminalMetadata(string? content)
    {
        try
        {
            using var json = JsonDocument.Parse(content ?? "{}");
            if (json.RootElement.ValueKind != JsonValueKind.Object) return default;
            var digest = StringArg(json.RootElement, "verification_identity");
            if (digest is not { Length: 64 } || !digest.All(char.IsAsciiHexDigit)) return default;
            var completedState = !json.RootElement.TryGetProperty("state", out var state)
                || state.ValueKind == JsonValueKind.Null
                || state.ValueKind == JsonValueKind.String && state.GetString() == "exited";
            var validBackground = !json.RootElement.TryGetProperty("background", out var background)
                || background.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False;
            var stillRunning = StringArg(json.RootElement, "state") is null && background.ValueKind == JsonValueKind.True;
            int? completedReadExit = null;
            if (StringArg(json.RootElement, "terminal_effect") == "read_only"
                && json.RootElement.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number
                && exit.TryGetInt32(out var code)
                && completedState && validBackground && !stillRunning)
                completedReadExit = code;
            var repair = StringArg(json.RootElement, "verification_repair_identity");
            if (repair is not { Length: 64 } || !repair.All(char.IsAsciiHexDigit)) repair = null;
            var invalid = repair is not null && json.RootElement.TryGetProperty("verification_invocation_invalid", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            var cwd = StringArg(json.RootElement, "verification_cwd");
            if (string.IsNullOrWhiteSpace(cwd) || cwd.Length > RuAgent.Protocol.TerminalLimits.MaxWorkingDirectoryChars) cwd = null;
            var directoryIdentity = cwd is null ? null : Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(cwd).ToUpperInvariant())));
            return new(digest.ToUpperInvariant(), completedReadExit, repair?.ToUpperInvariant(), invalid, directoryIdentity, cwd);
        }
        catch (JsonException) { return default; }
    }

    private static string CommandIdentity(JsonElement args, int expectedExit)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (string name in new[] { "executable", "script", "shell" })
                if (args.TryGetProperty(name, out var value)) { writer.WritePropertyName(name); value.WriteTo(writer); }
            writer.WritePropertyName("arguments");
            if (args.TryGetProperty("arguments", out var arguments)) arguments.WriteTo(writer);
            else { writer.WriteStartArray(); writer.WriteEndArray(); }
            writer.WriteString("mode", StringArg(args, "mode") ?? "run");
            writer.WriteString("working_directory", StringArg(args, "working_directory") ?? ".");
            writer.WriteNumber("expected_exit_code", expectedExit);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

}

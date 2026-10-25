using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    private readonly record struct TerminalMetadata(string? CanonicalIdentity, bool IsReadOnlyCompleted);

    private static TerminalMetadata ReadTerminalMetadata(string? content)
    {
        try
        {
            using var json = JsonDocument.Parse(content ?? "{}");
            if (json.RootElement.ValueKind != JsonValueKind.Object) return default;
            var digest = StringArg(json.RootElement, "verification_identity");
            if (digest is not { Length: 64 } || !digest.All(char.IsAsciiHexDigit)) return default;
            var readOnly = StringArg(json.RootElement, "terminal_effect") == "read_only"
                && json.RootElement.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number
                && exit.TryGetInt32(out var code) && code == 0
                && StringArg(json.RootElement, "state") is null or "exited"
                && !(StringArg(json.RootElement, "state") is null
                    && json.RootElement.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True);
            return new(digest.ToUpperInvariant(), readOnly);
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

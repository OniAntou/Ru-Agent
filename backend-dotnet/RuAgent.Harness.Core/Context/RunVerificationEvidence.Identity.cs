using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    private static string? ReceiptIdentity(string? content, int expectedExit)
    {
        try
        {
            using var json = JsonDocument.Parse(content ?? "{}");
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var digest = StringArg(json.RootElement, "verification_identity");
            return digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit)
                ? "canonical:" + digest.ToUpperInvariant() + ":" + expectedExit : null;
        }
        catch (JsonException) { return null; }
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

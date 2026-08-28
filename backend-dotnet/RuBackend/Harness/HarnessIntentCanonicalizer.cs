using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuBackend.Harness;

/// <summary>
/// Produces one stable representation for tool arguments at every approval and
/// execution boundary. JSON object order is not significant, while array order is.
/// Duplicate object properties are rejected instead of silently choosing a value.
/// </summary>
internal static class HarnessIntentCanonicalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeHash(string toolId, string toolVersion, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        var canonicalArguments = Canonicalize(arguments);
        var input = $"{toolId}|{toolVersion}|{canonicalArguments}";
        return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(input))).ToLowerInvariant();
    }

    public static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            WriteCanonical(writer, value);
        }

        return StrictUtf8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 1; index < properties.Length; index++)
                {
                    if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
                    {
                        throw new JsonException("duplicate_property");
                    }
                }

                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                // JsonElement.WriteTo preserves the exact finite JSON number while
                // still avoiding culture-dependent formatting.
                value.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("unsupported_json_value");
        }
    }
}

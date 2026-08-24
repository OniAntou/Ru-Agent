using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence.Privacy;

public sealed class KnownSecretRedactor : ITransientContentRedactor
{
    public const string Replacement = "[REDACTED]";
    private const int MaximumSecrets = 256;
    private const int MaximumSecretCharacters = 4096;
    private readonly string[] _secrets;
    private readonly Regex? _pattern;
    private readonly int _maximumSecretLength;

    public KnownSecretRedactor(IEnumerable<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(knownSecrets);
        _secrets = knownSecrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToArray();
        if (_secrets.Length > MaximumSecrets
            || _secrets.Any(secret => secret.Length > MaximumSecretCharacters))
        {
            throw new ArgumentException("Known secret set exceeds its bound.", nameof(knownSecrets));
        }

        _maximumSecretLength = _secrets.Length == 0 ? 0 : _secrets[0].Length;
        _pattern = _secrets.Length == 0
            ? null
            : new Regex(
                string.Join('|', _secrets.Select(Regex.Escape)),
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    public static KnownSecretRedactor Empty { get; } = new(Array.Empty<string>());

    public KnownSecretRedactionSession CreateSession() =>
        new(_pattern, _maximumSecretLength);

    ITransientContentRedactionSession ITransientContentRedactor.CreateSession() =>
        CreateSession();

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var session = CreateSession();
        return string.Concat(session.Append(value), session.Complete());
    }

    internal byte[] RedactPayload(
        HarnessPayloadEncoding encoding,
        ReadOnlyMemory<byte> plaintext)
    {
        if (_secrets.Length == 0)
        {
            return plaintext.ToArray();
        }

        return encoding switch
        {
            HarnessPayloadEncoding.Utf8Text =>
                Encoding.UTF8.GetBytes(Redact(StrictUtf8(plaintext))),
            HarnessPayloadEncoding.Json => RedactJson(plaintext),
            _ => throw new HarnessStoreException("payload_encoding_not_redactable"),
        };
    }

    private byte[] RedactJson(ReadOnlyMemory<byte> plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedactedJson(writer, document.RootElement);
            }

            return stream.ToArray();
        }
        catch (JsonException exception)
        {
            throw new HarnessStoreException("payload_json_invalid", exception);
        }
    }

    private void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Redact(property.Name));
                    WriteRedactedJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                break;
            default:
                throw new HarnessStoreException("payload_json_invalid");
        }
    }

    private static string StrictUtf8(ReadOnlyMemory<byte> value)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(value.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HarnessStoreException("payload_utf8_invalid", exception);
        }
    }
}

public sealed class KnownSecretRedactionSession : ITransientContentRedactionSession
{
    private readonly Regex? _pattern;
    private readonly int _maximumSecretLength;
    private readonly StringBuilder _pending = new();
    private bool _completed;

    internal KnownSecretRedactionSession(Regex? pattern, int maximumSecretLength)
    {
        _pattern = pattern;
        _maximumSecretLength = maximumSecretLength;
    }

    public string Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_completed)
        {
            throw new InvalidOperationException("redaction_session_completed");
        }

        _pending.Append(chunk);
        return Drain(final: false);
    }

    public string Complete()
    {
        if (_completed)
        {
            return string.Empty;
        }

        _completed = true;
        return Drain(final: true);
    }

    private string Drain(bool final)
    {
        if (_pattern is null)
        {
            var passthrough = _pending.ToString();
            _pending.Clear();
            return passthrough;
        }

        var combined = _pending.ToString();
        _pending.Clear();
        var safeStartLimit = final
            ? combined.Length
            : Math.Max(0, combined.Length - _maximumSecretLength + 1);
        if (safeStartLimit == 0)
        {
            _pending.Append(combined);
            return string.Empty;
        }

        var output = new StringBuilder(safeStartLimit);
        var consumed = 0;
        foreach (Match match in _pattern.Matches(combined))
        {
            if (match.Index >= safeStartLimit)
            {
                break;
            }

            output.Append(combined, consumed, match.Index - consumed);
            output.Append(KnownSecretRedactor.Replacement);
            consumed = match.Index + match.Length;
        }

        var tailStart = consumed;
        if (consumed < safeStartLimit)
        {
            output.Append(combined, consumed, safeStartLimit - consumed);
            tailStart = safeStartLimit;
        }

        if (tailStart < combined.Length)
        {
            _pending.Append(combined, tailStart, combined.Length - tailStart);
        }

        return output.ToString();
    }
}

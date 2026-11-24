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

    /// <summary>
    /// The shortest stored credential taken as a seed. Provider keys are far longer;
    /// a short value would redact ordinary words wherever it happened to appear.
    /// </summary>
    public const int MinimumStoredSecretCharacters = 12;

    private readonly string[] _baseSecrets;
    private readonly HashSet<string> _storedSecrets = new(StringComparer.Ordinal);
    private readonly object _storedGate = new();
    private readonly bool _acceptsStoredSecrets;
    private volatile SeedSet _seeds;

    /// <summary>One immutable seed set; sessions and payloads keep the set they started with.</summary>
    private sealed record SeedSet(int Count, Regex? Pattern, string[] Secrets);

    public KnownSecretRedactor(IEnumerable<string> knownSecrets)
        : this(knownSecrets, acceptsStoredSecrets: true)
    {
    }

    private KnownSecretRedactor(IEnumerable<string> knownSecrets, bool acceptsStoredSecrets)
    {
        ArgumentNullException.ThrowIfNull(knownSecrets);
        _baseSecrets = knownSecrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_baseSecrets.Length > MaximumSecrets
            || _baseSecrets.Any(secret => secret.Length > MaximumSecretCharacters))
        {
            throw new ArgumentException("Known secret set exceeds its bound.", nameof(knownSecrets));
        }

        _acceptsStoredSecrets = acceptsStoredSecrets;
        _seeds = Build(_baseSecrets);
    }

    public static KnownSecretRedactor Empty { get; } = new(Array.Empty<string>(), acceptsStoredSecrets: false);

    /// <summary>
    /// Adds credentials the Host read from its vault, such as the provider key a run is
    /// about to use, so the key is redacted from model output, tool results and live
    /// frames even though no environment variable carries it. Seeds are only added,
    /// never dropped: a replaced key still deserves redaction. Values shorter than
    /// <see cref="MinimumStoredSecretCharacters"/>, longer than the bound, or past the
    /// seed limit are ignored rather than failing the caller.
    /// </summary>
    public void AddStoredSecrets(IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (!_acceptsStoredSecrets) return;
        lock (_storedGate)
        {
            var added = false;
            foreach (var secret in secrets)
            {
                if (secret is null
                    || secret.Length < MinimumStoredSecretCharacters
                    || secret.Length > MaximumSecretCharacters
                    || string.IsNullOrWhiteSpace(secret)
                    || _baseSecrets.Length + _storedSecrets.Count >= MaximumSecrets)
                {
                    continue;
                }

                added |= _storedSecrets.Add(secret);
            }

            if (added) _seeds = Build(_baseSecrets.Concat(_storedSecrets));
        }
    }

    private static SeedSet Build(IEnumerable<string> secrets)
    {
        var ordered = secrets
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToArray();
        return new SeedSet(
            ordered.Length,
            ordered.Length == 0
                ? null
                : new Regex(
                    string.Join('|', ordered.Select(Regex.Escape)),
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
            ordered);
    }

    public KnownSecretRedactionSession CreateSession() => CreateSession(_seeds);

    private static KnownSecretRedactionSession CreateSession(SeedSet seeds) =>
        new(seeds.Pattern, seeds.Secrets);

    ITransientContentRedactionSession ITransientContentRedactor.CreateSession() =>
        CreateSession();

    public string Redact(string value) => Redact(_seeds, value);

    private static string Redact(SeedSet seeds, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var session = CreateSession(seeds);
        return string.Concat(session.Append(value), session.Complete());
    }

    internal byte[] RedactPayload(
        HarnessPayloadEncoding encoding,
        ReadOnlyMemory<byte> plaintext)
    {
        var seeds = _seeds;
        if (seeds.Count == 0)
        {
            return plaintext.ToArray();
        }

        return encoding switch
        {
            HarnessPayloadEncoding.Utf8Text =>
                Encoding.UTF8.GetBytes(Redact(seeds, StrictUtf8(plaintext))),
            HarnessPayloadEncoding.Json => RedactJson(seeds, plaintext),
            _ => throw new HarnessStoreException("payload_encoding_not_redactable"),
        };
    }

    private static byte[] RedactJson(SeedSet seeds, ReadOnlyMemory<byte> plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedactedJson(seeds, writer, document.RootElement);
            }

            return stream.ToArray();
        }
        catch (JsonException exception)
        {
            throw new HarnessStoreException("payload_json_invalid", exception);
        }
    }

    private static void WriteRedactedJson(SeedSet seeds, Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Redact(seeds, property.Name));
                    WriteRedactedJson(seeds, writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(seeds, writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(seeds, element.GetString() ?? string.Empty));
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
    private readonly string[] _secrets;
    private readonly StringBuilder _pending = new();
    private bool _completed;

    internal KnownSecretRedactionSession(Regex? pattern, string[] secrets)
    {
        _pattern = pattern;
        _secrets = secrets;
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

    private int LongestPossibleSecretSuffix(string combined)
    {
        var best = 0;
        foreach (var secret in _secrets)
        {
            var first = secret[0];
            // A suffix as long as the whole secret is a complete match, which the
            // pattern already handles; only a strictly shorter run can still grow.
            var start = Math.Max(0, combined.Length - secret.Length + 1);
            for (var position = start; position < combined.Length; position++)
            {
                if (combined[position] != first) continue;
                var length = combined.Length - position;
                if (length <= best) break;
                if (combined.AsSpan(position).SequenceEqual(secret.AsSpan(0, length)))
                {
                    best = length;
                    break;
                }
            }
        }

        return best;
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
        // Only a trailing run that a later chunk could still turn into a secret is held:
        // the longest suffix that is a prefix of some secret. Holding a fixed
        // longest-secret-minus-one characters kept the end of every streamed line until
        // the next one arrived, so a running command's newest line showed a stub such as
        // "[7/" for a whole second whenever a provider key was configured (the Broker's
        // ChunkRedactor fixed the same delay for its side of the stream).
        var safeStartLimit = final
            ? combined.Length
            : combined.Length - LongestPossibleSecretSuffix(combined);
        if (safeStartLimit == 0)
        {
            _pending.Append(combined);
            return string.Empty;
        }

        var output = new StringBuilder(safeStartLimit);
        var consumed = 0;
        foreach (Match match in _pattern.Matches(combined))
        {
            // A match reaching into the held run may still grow into a longer,
            // overlapping secret: keep it pending with the rest of that run.
            if (match.Index >= safeStartLimit || match.Index + match.Length > safeStartLimit)
            {
                safeStartLimit = Math.Min(safeStartLimit, match.Index);
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

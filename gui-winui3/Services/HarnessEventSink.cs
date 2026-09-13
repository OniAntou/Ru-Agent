using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RuApp.Services;

public class HarnessEventSink : IDisposable
{
    private const int MaxBytes = 64 * 1024;
    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "agent.activity",
        "agent.approval.required",
        "agent.cancelled",
    };
    private static readonly string[] ForbiddenKeyParts =
    {
        "token",
        "secret",
        "audio",
        "transcript",
        "prompt",
        "response",
        "session_id",
        "request_body",
        "argument",
    };

    private readonly object _gate = new();
    private readonly string? _eventsPath;
    private readonly List<string> _codes = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _bytesWritten;
    private bool _disposed;

    public HarnessEventSink(string eventsPath)
    {
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            throw new ArgumentException("Harness event path is required.", nameof(eventsPath));
        }

        _eventsPath = Path.GetFullPath(eventsPath);
    }

    protected HarnessEventSink()
    {
    }

    public IReadOnlyList<string> Codes
    {
        get
        {
            lock (_gate)
            {
                return _codes.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_counts, StringComparer.Ordinal);
            }
        }
    }

    public bool IsFailed { get; private set; }

    public virtual void Write(string code, object? fields = null)
    {
        var scalarFields = NormalizeFields(fields);
        if (!AllowedCodes.Contains(code))
        {
            throw new ArgumentException("Harness event code is not allowlisted.", nameof(code));
        }

        var line = JsonSerializer.Serialize(new { code, fields = scalarFields }) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(line);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bytesWritten + bytes > MaxBytes)
            {
                IsFailed = true;
                return;
            }

            if (_eventsPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_eventsPath)!);
                File.AppendAllText(_eventsPath, line, Encoding.UTF8);
            }

            _bytesWritten += bytes;
            _codes.Add(code);
            _counts[code] = _counts.GetValueOrDefault(code) + 1;
        }
    }

    public virtual void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private static Dictionary<string, object?> NormalizeFields(object? fields)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fields is null)
        {
            return result;
        }

        if (fields is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                AddScalar(result, entry.Key?.ToString(), entry.Value);
            }

            return result;
        }

        foreach (PropertyInfo property in fields.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            AddScalar(result, property.Name, property.GetValue(fields));
        }

        return result;
    }

    private static void AddScalar(Dictionary<string, object?> destination, string? key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Harness event field names must be non-empty.", nameof(key));
        }

        string normalized = key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        if (ForbiddenKeyParts.Any(part => normalized.Contains(part, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Harness event contains private data.", key);
        }

        if (value is not null
            and not string
            and not bool
            and not byte
            and not sbyte
            and not short
            and not ushort
            and not int
            and not uint
            and not long
            and not ulong
            and not float
            and not double
            and not decimal
            and not Enum)
        {
            throw new ArgumentException("Harness event fields must be scalar.", key);
        }

        if (value is string text && text.Length > 256)
        {
            throw new ArgumentException("Harness event string is too long.", key);
        }

        if (value is double doubleValue && !double.IsFinite(doubleValue)
            || value is float floatValue && !float.IsFinite(floatValue))
        {
            throw new ArgumentException("Harness event number is not finite.", key);
        }

        destination[key] = value is Enum enumValue
            ? enumValue.ToString()
            : value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value;
    }
}

public sealed class InMemoryHarnessEventSink : HarnessEventSink
{
    public InMemoryHarnessEventSink()
    {
    }
}

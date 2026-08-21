namespace RuBackend.Diagnostics;

using System.Text.RegularExpressions;

public static class CorrelationContext
{
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_-]{8,64}$", RegexOptions.Compiled);
    private static readonly AsyncLocal<string?> CurrentCorrelationId = new();

    public static string? Current => CurrentCorrelationId.Value;

    public static string GenerateId() => $"c_{Guid.NewGuid():N}";

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 8 or > 64)
        {
            return false;
        }

        return ValidPattern.IsMatch(id);
    }

    public static string Sanitize(string? id)
    {
        if (IsValid(id))
        {
            return id!.Trim();
        }

        return GenerateId();
    }

    public static IDisposable Push(string? correlationId)
    {
        var sanitized = string.IsNullOrWhiteSpace(correlationId) ? null : Sanitize(correlationId);
        var previous = CurrentCorrelationId.Value;
        CurrentCorrelationId.Value = sanitized;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CurrentCorrelationId.Value = _previous;
                _disposed = true;
            }
        }
    }
}

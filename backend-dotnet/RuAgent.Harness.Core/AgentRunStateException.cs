namespace RuAgent.Harness.Core;

/// <summary>
/// A run transition or commit a bounded code names: a lost optimistic-concurrency race,
/// a contract guard, or a missing run row. It derives from
/// <see cref="InvalidOperationException"/> because callers already treat a refused
/// transition as one, while the code is a stable terminal-code-shaped token so a fault that
/// reaches the runtime drive boundary can name its family instead of falling back to the
/// driver catch-all.
/// </summary>
public sealed class AgentRunStateException : InvalidOperationException
{
    public AgentRunStateException(string code)
        : base(code)
    {
        Code = ValidateCode(code);
    }

    public string Code { get; }

    private static string ValidateCode(string? code)
    {
        if (code is not { Length: >= 1 and <= 64 }
            || code[0] is not (>= 'a' and <= 'z')
            || code.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '.'))
        {
            throw new ArgumentException("Run state code must be a bounded safe token.", nameof(code));
        }

        return code;
    }
}
